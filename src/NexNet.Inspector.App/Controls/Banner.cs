using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;

namespace NexNet.Inspector.App.Controls;

/// <summary>The three banner layouts of controls/banner.</summary>
public enum BannerKind : byte
{
    /// <summary>A status line: raised fill, 6 px radius, 7 px dot (or spinner) and 11 px secondary text (connect).</summary>
    Status = 0,

    /// <summary>The 34 px full-width strip under the title bar when a live session ended, with its actions.</summary>
    SessionEnded = 1,

    /// <summary>The warning card: "!" badge, bold title, secondary body (open-recording schema mismatch).</summary>
    SchemaMismatch = 2,
}

/// <summary>What a status line reports: the dot's colour, or the spinner (OQ2).</summary>
public enum StatusTone : byte
{
    /// <summary>Tertiary dot: informational (the session-ended strip).</summary>
    Neutral = 0,

    /// <summary>status.ok dot: reached, ready.</summary>
    Ok = 1,

    /// <summary>A spinner: connecting, checking.</summary>
    Busy = 2,

    /// <summary>status.error dot: refused, unreachable, failed.</summary>
    Error = 3,
}

/// <summary>
/// An inline, non-modal notice (controls/banner). <see cref="ContentControl.Content"/> is the detail text (a string,
/// or a TextBlock with mono runs for hashes); <see cref="Title"/> the bold lead; <see cref="Actions"/> the strip's
/// buttons. The kind and tone are pseudo-classes (<c>:status</c>, <c>:sessionended</c>, <c>:schemamismatch</c>;
/// <c>:ok</c>, <c>:busy</c>, <c>:error</c>) that Themes/Banner.axaml styles. A status line is announced politely, the
/// session-ended strip assertively (once, when it appears).
/// </summary>
[PseudoClasses(":status", ":sessionended", ":schemamismatch", ":ok", ":busy", ":error")]
public sealed class Banner : ContentControl
{
    public static readonly StyledProperty<BannerKind> KindProperty =
        AvaloniaProperty.Register<Banner, BannerKind>(nameof(Kind));

    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<Banner, StatusTone>(nameof(Tone));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<Banner, string?>(nameof(Title));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<Banner, object?>(nameof(Actions));

    public Banner()
    {
        UpdateKind(BannerKind.Status);
        UpdateTone(StatusTone.Neutral);
    }

    public BannerKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) UpdateKind(change.GetNewValue<BannerKind>());
        else if (change.Property == ToneProperty) UpdateTone(change.GetNewValue<StatusTone>());
    }

    private void UpdateKind(BannerKind kind)
    {
        PseudoClasses.Set(":status", kind == BannerKind.Status);
        PseudoClasses.Set(":sessionended", kind == BannerKind.SessionEnded);
        PseudoClasses.Set(":schemamismatch", kind == BannerKind.SchemaMismatch);
        AutomationProperties.SetLiveSetting(this, kind == BannerKind.SessionEnded ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
    }

    private void UpdateTone(StatusTone tone)
    {
        PseudoClasses.Set(":ok", tone == StatusTone.Ok);
        PseudoClasses.Set(":busy", tone == StatusTone.Busy);
        PseudoClasses.Set(":error", tone == StatusTone.Error);
    }
}
