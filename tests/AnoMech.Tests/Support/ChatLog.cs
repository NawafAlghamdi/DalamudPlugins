using System.Reflection;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace AnoMech.Tests;

// Plugin.ChatGui for a headless run: keeps the text of every line printed.
public class ChatLog : DispatchProxy
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines => lines;

    public static ChatLog Create() => (ChatLog)(object)Create<IChatGui, ChatLog>();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IChatGui.Print) && args is [var first, ..])
        {
            switch (first)
            {
                case XivChatEntry entry: lines.Add(entry.Message.TextValue); break;
                case string text: lines.Add(text); break;
            }
        }
        return method is { ReturnType.IsValueType: true } && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
