using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public partial class TileManager : Node
{
	/// <summary>Maximum number of HTTP requests running at the same time.</summary>
	private const int MaxConcurrentRequests = 10;
	/// <summary>Show the load screen while more than this many downloads are queued.</summary>
	private const int LoadScreenThreshold = 10;
	/// <summary>Seconds before a hanging request is aborted.</summary>
	private const double RequestTimeoutSeconds = 30;
	/// <summary>Many tile servers (e.g. OSM) reject requests without a User-Agent.</summary>
	private const string UserAgent = "TileDownloader/1.0 (Godot)";

	[ExportGroup("Tile Manager")]
	[ExportSubgroup("Map UI")]
	[Export] Control layout2;
	[Export] Control layout4;
	[Export] ButtonGroup group;
	/// <summary>
	///  The tile we are zoomed in on, if any data exists for it.
	/// Only relevant for zoom levels higher than 0.
	/// </summary>
	[Export] TextureRect tileBackground;

	[ExportSubgroup("Connection")]
	[Export] MenuButton urlPresets;
	[Export] TextEdit tileFormat;
	[Export] Control loadScreen;

	[ExportSubgroup("Labels")]
	[Export] Label batchZoomLevel;
	[Export] Label batchDownloadCount;
	[Export] Label zoomLevel;
	[Export] Label sizeApprox;
	[Export] Label xCoords;
	[Export] Label yCoords;

	/// <summary>
	/// Scheduled downloads for batch downloading. X, Y are tile coordinates, Z is the zoom level.
	/// </summary>
	private readonly Queue<Vector3I> scheduledDownloads = new();
	/// <summary>
	/// Tiles currently being downloaded, to avoid requesting the same tile twice.
	/// </summary>
	private readonly HashSet<Vector3I> inFlightDownloads = new();
	/// <summary>
	/// Number of HTTP requests currently running.
	/// </summary>
	private int activeRequests = 0;
	/// <summary>
	/// Approximate size of the downloaded tiles on disk, in bytes.
	/// </summary>
	private long tilesSizeBytes = 0;
	/// <summary>
	/// Depth of zoom to download when batch downloading.
	/// </summary>
	private int downloadBatchDepth = 1;
	/// <summary>
	/// Current map zoom.
	/// </summary>
	int currentZoom => tilePath.Count;
	/// <summary>
	/// The coordinates of the parent tiles, as we traverse the map.
	/// The top element is the parent of the tile we're looking at.
	/// If the stack is empty, we're at zoom level 0.
	/// </summary>
	Stack<Vector2I> tilePath = [];
	/// <summary>
	/// Coordinates of the tile we are currently viewing.
	/// </summary>
	Vector2I currentTile = new Vector2I(-1, -1);
	/// <summary>
	/// Currently selected child tile.
	/// All actions apply to this tile.
	/// </summary>
	Tile targetTile = null;

	private bool is2TileLayout => (layout2.GetChild(1, false) as Control).Visible;

	public class JSONSourceData
	{
		public JSONSource[] sources { get; set; }
	}
	public class JSONSource
	{
		public string name { get; set; }
		public string url { get; set; }
	}

	public override void _Ready()
	{
		base._Ready();

		LoadTile(currentTile);

		LoadSources("./sources.json");

		#region buttons init

		group.Pressed += (btn) =>
		{
			// On button press, set target tile for actions.
			if (btn is not Tile t)
				return;

			targetTile = t;
		};

		var btns = group.GetButtons();
		for (int i = 0; i < btns.Count; i++)
		{
			var tile = btns[i] as Tile;
			tile.onDoubleClicked += () =>
			{
				tile.ButtonPressed = true;
				targetTile = tile;
				ZoomIn();
			};
			tile.onDownloadClicked += () =>
			{
				tile.ButtonPressed = true;
				targetTile = tile;
				StartDownload(tile.coords, currentZoom);
			};
		}

		#endregion

		InitSizeLabel();
	}

	/// <summary>
	/// Populates the URL presets menu from a JSON file, if it exists.
	/// </summary>
	private void LoadSources(string sourcesPath)
	{
		if (!File.Exists(sourcesPath))
			return;

		JSONSource[] sources;
		try
		{
			var sourceText = File.ReadAllText(sourcesPath);
			var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
			sources = JsonSerializer.Deserialize<JSONSourceData>(sourceText, options)?.sources;
		}
		catch (Exception e)
		{
			GD.PushError($"Failed to load {sourcesPath}: {e.Message}");
			return;
		}

		if (sources == null)
		{
			GD.PushWarning($"{sourcesPath} has no \"sources\" array.");
			return;
		}

		var popup = urlPresets.GetPopup();
		// Populate the presets menu.
		foreach (var source in sources)
		{
			popup.AddItem(source.name ?? source.url ?? "?");
		}
		// On menu item pressed.
		popup.IndexPressed += (index) => tileFormat.Text = sources[(int)index].url ?? "";
	}

	private async void InitSizeLabel()
	{
		try
		{
			// Awaiting resumes on the main thread, so it's safe to touch the UI afterwards.
			tilesSizeBytes += await CheckSize();
		}
		catch (Exception e)
		{
			GD.PushError($"Failed to compute tiles size: {e.Message}");
		}
		UpdateSizeLabel();
	}

	private void UpdateSizeLabel()
	{
		var sizes = new string[]
		{
			" bytes",
			" KBytes",
			" MBytes",
		};

		var size = tilesSizeBytes;
		for (int i = 0; i < sizes.Length; i++)
		{
			if (size < 1024)
			{
				sizeApprox.Text = size.ToString() + sizes[i];
				return;
			}
			size /= 1024;
		}
		sizeApprox.Text = size.ToString() + " GBytes";
	}


	public async Task<long> CheckSize()
	{
		if (!Directory.Exists("./Tiles"))
			return 0;
		DirectoryInfo dirInfo = new DirectoryInfo("./Tiles");
		long dirSize = await Task.Run(() => dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length));
		return dirSize;
	}

	private string GetFilePath(Vector2I coords, int zoom, string type, bool createDir)
	{
		var dir = $"./Tiles/{zoom}/{coords.X}";
		if (createDir)
		{
			Directory.CreateDirectory(dir);
		}
		return dir + $"/{coords.Y}." + type;
	}

	public void ResetZoom()
	{
		while (currentZoom > 0)
		{
			currentTile = tilePath.Pop();
		}
		OnZoomChanged();
		LoadTile(currentTile);
	}

	public void ZoomOut()
	{
		if (currentZoom <= 0)
		{
			// We can zoom no more.
			return;
		}
		currentTile = tilePath.Pop();
		OnZoomChanged();
		LoadTile(currentTile);
	}

	public void ZoomIn()
	{
		if (targetTile == null)
		{
			return;
		}
		tilePath.Push(currentTile);
		currentTile = targetTile.coords;
		OnZoomChanged();
		LoadTile(currentTile);
	}

	private void OnZoomChanged()
	{
		zoomLevel.Text = $"{currentZoom}";
		xCoords.Text = currentTile.X.ToString();
		yCoords.Text = currentTile.Y.ToString();
		targetTile = null;
		var btns = group.GetButtons();
		for (int i = 0; i < btns.Count; i++)
		{
			btns[i].ButtonPressed = false;
		}
	}

	private string FormatGPSAngle(float decimalGPS, bool isLon)
	{
		string letter;
		if (isLon)
		{
			letter = decimalGPS < 0 ? "W" : "E";
		}
		else
		{
			letter = decimalGPS < 0 ? "S" : "N";
		}

		decimalGPS = Math.Abs(decimalGPS);
		var degs = (int)decimalGPS;
		var minutes = (decimalGPS - degs) * 60;
		var seconds = (minutes - (int)minutes) * 60;
		var secondsFraction = (int)((seconds - (int)seconds) * 1000);

		if (isLon)
		{
			return $"{degs:000}°{(int)minutes:00}'{(int)seconds:00}.{secondsFraction:000}\"" + letter;
		}
		else
		{
			return $"{degs:00}°{(int)minutes:00}'{(int)seconds:00}.{secondsFraction:000}\"" + letter;
		}
	}


	private void LoadTile(Vector2I parentTile)
	{
		// Set the correct layout, 2 or 4.
		layout2.Visible = currentZoom <= 0;
		layout4.Visible = currentZoom > 0;
		var activeLayout = layout2.Visible ? layout2 : layout4;
		// Set number labels.
		var coords = GetChildrenTilesCoords(parentTile, currentZoom);
		var children = activeLayout.GetChildren();
		for (int i = 0; i < coords.Length; i++)
		{
			var tile = children[i].GetChild<Tile>();

			// Set lon/lan labels.
			tile.SetData(coords[i]);
			// Load images of children, if we have them.
			tile.SetTexture(TryLoadTexture(coords[i], currentZoom, out var t) ? t : null);
		}

		// Load background image, if we have it.
		tileBackground.Texture = TryLoadTexture(parentTile, currentZoom - 1, out var bt) ? bt : null;
	}

	private bool TryLoadTexture(Vector2I coords, int zoom, out ImageTexture t)
	{
		t = null;
		if (!HasTile(coords, zoom, out var filePath))
			return false;

		var img = Image.LoadFromFile(filePath);
		if (img == null)
			return false;

		t = ImageTexture.CreateFromImage(img);
		return true;
	}


	public bool HasTile(Vector2I coords, int zoom, out string path)
	{
		path = null;
		string[] fileTypes = ["jpg", "png"];
		for (int j = 0; j < fileTypes.Length; j++)
		{
			var filePath = GetFilePath(coords, zoom, fileTypes[j], false);
			if (File.Exists(filePath))
			{
				path = filePath;
				return true;
			}
		}
		return false;
	}

	public void DownloadTile()
	{
		if (targetTile == null)
			return;
		StartDownload(targetTile.coords, currentZoom);
	}

	public void DownloadTileRecursive()
	{
		if (targetTile == null)
		{
			return;
		}

		var coords = new List<List<Vector2I>>
		{
			new List<Vector2I>([targetTile.coords])
		};

		for (int zoom = currentZoom + 1, i = 0; zoom < currentZoom + downloadBatchDepth + 1; zoom++, i++)
		{
			var res = new List<Vector2I>();
			coords[i].ForEach(prevLayerTile =>
			{
				res.AddRange(GetChildrenTilesCoords(prevLayerTile, zoom));
			});

			coords.Add(res);
		}

		for (int i = 0; i < coords.Count; i++)
		{
			var zoom = currentZoom + i;
			for (int j = 0; j < coords[i].Count; j++)
			{
				scheduledDownloads.Enqueue(new Vector3I(coords[i][j].X, coords[i][j].Y, zoom));
			}
		}
	}

	public override void _Process(double delta)
	{
		base._Process(delta);
		while (scheduledDownloads.Count > 0 && activeRequests < MaxConcurrentRequests)
		{
			var dl = scheduledDownloads.Dequeue();
			StartDownload(new Vector2I(dl.X, dl.Y), dl.Z);
		}
		loadScreen.Visible = scheduledDownloads.Count > LoadScreenThreshold;
	}


	// MARK: Download
	private void StartDownload(Vector2I coords, int zoom)
	{
		var key = new Vector3I(coords.X, coords.Y, zoom);
		if (inFlightDownloads.Contains(key) || HasTile(coords, zoom, out var _))
		{
			return;
		}

		var url = tileFormat.Text
			.Replace("{x}", coords.X.ToString())
			.Replace("{y}", coords.Y.ToString())
			.Replace("{z}", zoom.ToString());

		var request = new HttpRequest { Timeout = RequestTimeoutSeconds };
		AddChild(request);
		inFlightDownloads.Add(key);
		activeRequests++;

		void Finish()
		{
			inFlightDownloads.Remove(key);
			activeRequests--;
			request.QueueFree();
		}

		request.RequestCompleted += (result, responseCode, headers, body) =>
		{
			try
			{
				OnDownloadCompleted(coords, zoom, url, (HttpRequest.Result)(int)result, responseCode, body);
			}
			catch (Exception e)
			{
				GD.PushError($"Failed to save tile {url}: {e.Message}");
			}
			finally
			{
				Finish();
			}
		};

		var err = request.Request(url, [$"User-Agent: {UserAgent}"]);
		if (err != Error.Ok)
		{
			GD.PushError($"Failed to start request for {url}: {err}");
			Finish();
		}
	}

	private void OnDownloadCompleted(Vector2I coords, int zoom, string url, HttpRequest.Result result, long responseCode, byte[] body)
	{
		if (result != HttpRequest.Result.Success || responseCode != 200)
		{
			GD.PushWarning($"Tile download failed ({result}, HTTP {responseCode}): {url}");
			return;
		}

		// Detect the format from the data itself, headers are unreliable.
		var type = DetectImageType(body);
		if (type == null)
		{
			GD.PushWarning($"Tile is not a PNG or JPEG image: {url}");
			return;
		}

		var image = new Image();
		var loadErr = type == "png" ? image.LoadPngFromBuffer(body) : image.LoadJpgFromBuffer(body);
		if (loadErr != Error.Ok)
		{
			GD.PushWarning($"Tile image could not be decoded ({loadErr}): {url}");
			return;
		}

		// Save the original bytes as-is, re-encoding would lose quality.
		File.WriteAllBytes(GetFilePath(coords, zoom, type, true), body);
		tilesSizeBytes += body.Length;
		UpdateSizeLabel();

		// Show the texture if the tile is currently visible.
		if (zoom != currentZoom)
			return;
		var tile = group.GetButtons()
			.OfType<Tile>()
			.FirstOrDefault(t => t.coords == coords && t.IsVisibleInTree());
		tile?.SetTexture(ImageTexture.CreateFromImage(image));
	}

	/// <summary>
	/// Returns "png" or "jpg" based on the file signature, or null if it's neither.
	/// </summary>
	private static string DetectImageType(byte[] body)
	{
		if (body == null)
			return null;
		if (body.Length >= 8 && body[0] == 0x89 && body[1] == 0x50 && body[2] == 0x4E && body[3] == 0x47)
			return "png";
		if (body.Length >= 3 && body[0] == 0xFF && body[1] == 0xD8 && body[2] == 0xFF)
			return "jpg";
		return null;
	}

	public void SetDownloadRecZoom(int zoom)
	{
		downloadBatchDepth = zoom;
		batchZoomLevel.Text = downloadBatchDepth.ToString();

		// geometric series formula for sum of powers of X^i where i = 0 to n.
		// (X^(n+1) - 1) / (X - 1)
		// For X = 4, the formula is: (4^(n+1) - 1) / 3.
		var count = (int)((Math.Pow(4, zoom + 1) - 1) / 3);
		batchDownloadCount.Text = count.ToString();
	}

	private Vector2I[] GetChildrenTilesCoords(Vector2I parentCoord, int zoomLevel)
	{
		if (zoomLevel <= 0)
		{
			return [new Vector2I(0, 0), new Vector2I(1, 0)];
		}
		var x = parentCoord.X * 2;
		var y = parentCoord.Y * 2;
		return [
			new Vector2I(x, y),
			new Vector2I(x + 1, y),
			new Vector2I(x, y + 1),
			new Vector2I(x + 1, y + 1)
		];
	}
}
