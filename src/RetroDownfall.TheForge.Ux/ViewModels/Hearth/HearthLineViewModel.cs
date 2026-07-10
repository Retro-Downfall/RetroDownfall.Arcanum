using CommunityToolkit.Mvvm.ComponentModel;

namespace RetroDownfall.TheForge.Ux.ViewModels.Hearth;

/// <summary>One displayed line in The Hearth terminal output.</summary>
public sealed partial class HearthLineViewModel : ObservableObject
{

    public HearthLineViewModel(string text, HearthLineKind kind, DateTimeOffset? timestamp = null)
    {

        Text = text;

        Kind = kind;

        Timestamp = timestamp ?? DateTimeOffset.Now;

    }

    public string Text { get; }

    public HearthLineKind Kind { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsCommand => Kind == HearthLineKind.Command;

    public bool IsStandardError => Kind == HearthLineKind.StandardError;

    public bool IsSystem => Kind == HearthLineKind.System;

}
