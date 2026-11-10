using Godot;
using System;

/// <summary>
/// A button that also shows a map tile texture in a given coordinate, if it is available.
/// Can be double clicked to zoom into the tile. Shows a download button if the tile is not downloaded yet.
/// </summary>
public partial class Tile : Button
{
    [Export] Label xCoord;
    [Export] Label yCoord;
    [Export] TextureRect texture;
    [Export] Button downloadButton;
    public event Action onDoubleClicked;
    public event Action onDownloadClicked;
    public Vector2I coords { get; private set; }

    public override void _Ready()
    {
        base._Ready();
        downloadButton.Pressed += () =>
        {
            onDownloadClicked?.Invoke();
        };
    }

    public void SetData(Vector2I coordinates)
    {
        coords = coordinates;
        xCoord.Text = $"{coordinates.X}";
        yCoord.Text = $"{coordinates.Y}";
    }

    public void SetTexture(Texture2D tileTexture)
    {
        texture.Texture = tileTexture;
        downloadButton.Visible = tileTexture == null;
    }

    public override void _GuiInput(InputEvent @event)
    {
        base._GuiInput(@event);
        if (@event is InputEventMouseButton me && me.Pressed && me.DoubleClick && me.ButtonIndex == MouseButton.Left)
        {
            AcceptEvent();
            onDoubleClicked?.Invoke();
        }
    }
}
