using System.Linq;
using Godot;

public static class Extensions
{
    /// <summary>
    /// Get a child node of the given type.
    /// </summary>
    /// <param name="node"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T GetChild<T>(this Node node) where T : Node
    {
        var children = node.GetChildren();
        var child = children.FirstOrDefault(c => c is T);
        if (child != null) return (T)child;
        for (int i = 0; i < children.Count; i++)
        {
            var rec = children[i].GetChild<T>();
            if (rec != null)
                return rec;
        }
        return null;
    }
}