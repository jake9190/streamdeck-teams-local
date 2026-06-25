using System.Text;

namespace MsTeamsLocal;

/// <summary>
/// Renders modern, flat key icons as inline SVG and returns them as
/// <c>data:image/svg+xml;base64,…</c> URIs suitable for Stream Deck's setImage.
/// </summary>
public static class IconRenderer
{
    // Modern palette ----------------------------------------------------------
    private const string BgNeutral  = "#26282E"; // live / normal control
    private const string BgRed      = "#2C1A1D"; // muted / camera-off / leave tint
    private const string BgAmber    = "#2A2410"; // hand raised
    private const string BgGreen    = "#13241A"; // sharing
    private const string BgDisabled = "#161719"; // no meeting
    private const string BgLeave    = "#C8374A"; // leave (solid)

    private const string FgWhite    = "#F5F6F8";
    private const string FgRed      = "#FF5A5F";
    private const string FgAmber    = "#FFB020";
    private const string FgGreen    = "#3FB950";
    private const string FgBlue     = "#4C9AFF";
    private const string FgPink     = "#FF5A8A";
    private const string FgYellow   = "#FFC53D";
    private const string FgDisabled = "#52555E";

    /// <summary>Build the setImage data URI for an action given the current state.</summary>
    public static string Render(ActionDescriptor d, TeamsSnapshot s)
    {
        var svg = BuildSvg(d, s);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return "data:image/svg+xml;base64," + b64;
    }

    /// <summary>Resolved drawing inputs for an action+state, shared by the SVG and PNG renderers.</summary>
    public readonly record struct IconVisual(string Background, string Foreground, string GlyphPath, bool HandBadge);

    /// <summary>Decide background colour, glyph colour, glyph path and badge for an action+state.</summary>
    public static IconVisual GetVisual(ActionDescriptor d, TeamsSnapshot s)
    {
        // Disabled look when there's no meeting to act on (or Teams isn't running).
        // Reactions still show their own glyph (just dimmed) so each key stays identifiable.
        if (!s.TeamsRunning || !s.MeetingActive)
        {
            var disabledGlyph = d.Kind == ActionKind.Reaction ? ReactionGlyph(d.Id) : IdleGlyph(d.Kind);
            return new IconVisual(BgDisabled, FgDisabled, disabledGlyph, false);
        }

        return d.Kind switch
        {
            ActionKind.ToggleMute => new IconVisual(
                s.Muted ? BgRed : BgNeutral, s.Muted ? FgRed : FgWhite,
                s.Muted ? Paths.MicrophoneSlash : Paths.MicrophoneFill, false),
            ActionKind.ToggleCamera => new IconVisual(
                s.CameraOff ? BgRed : BgNeutral, s.CameraOff ? FgRed : FgWhite,
                s.CameraOff ? Paths.VideoCameraSlash : Paths.VideoCameraFill, false),
            ActionKind.RaiseHand => new IconVisual(
                s.HandRaised ? BgAmber : BgNeutral, s.HandRaised ? FgAmber : FgWhite,
                s.HandRaised ? Paths.HandFill : Paths.HandOutline, s.HandRaised),
            ActionKind.ShareScreen => new IconVisual(
                s.Sharing ? BgGreen : BgNeutral, s.Sharing ? FgGreen : FgWhite,
                s.Sharing ? Paths.MonitorFill : Paths.MonitorOutline, false),
            ActionKind.Leave => new IconVisual(BgLeave, FgWhite, Paths.PhoneDisconnect, false),
            ActionKind.Reaction => new IconVisual(BgNeutral, ReactionColor(d.Id), ReactionGlyph(d.Id), false),
            _ => new IconVisual(BgNeutral, FgWhite, Paths.MonitorOutline, false),
        };
    }

    /// <summary>Raw SVG markup (used for emitting the static manifest icon files).</summary>
    public static string BuildSvg(ActionDescriptor d, TeamsSnapshot s)
    {
        var v = GetVisual(d, s);
        var sb = new StringBuilder(2048);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\" viewBox=\"0 0 144 144\">");
        sb.Append("<rect x=\"6\" y=\"6\" width=\"132\" height=\"132\" rx=\"28\" fill=\"").Append(v.Background).Append("\"/>");
        sb.Append("<g transform=\"translate(31,31) scale(0.32)\" fill=\"").Append(v.Foreground).Append("\">");
        sb.Append("<path d=\"").Append(v.GlyphPath).Append("\"/>");
        sb.Append("</g>");
        if (v.HandBadge)
            sb.Append("<circle cx=\"112\" cy=\"32\" r=\"9\" fill=\"#FFC53D\" stroke=\"").Append(v.Background).Append("\" stroke-width=\"3\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static string IdleGlyph(ActionKind kind) => kind switch
    {
        ActionKind.ToggleMute => Paths.MicrophoneFill,
        ActionKind.ToggleCamera => Paths.VideoCameraFill,
        ActionKind.RaiseHand => Paths.HandOutline,
        ActionKind.ShareScreen => Paths.MonitorOutline,
        ActionKind.Leave => Paths.PhoneDisconnect,
        ActionKind.Reaction => Paths.SmileyFill,
        _ => Paths.MonitorOutline,
    };

    private static string ReactionGlyph(string id) => id switch
    {
        "react-like" => Paths.ThumbsUpFill,
        "react-love" => Paths.HeartFill,
        "react-applause" => Paths.HandsClappingFill,
        "react-laugh" => Paths.SmileyFill,
        "react-surprised" => Paths.SmileyOpenFill,
        _ => Paths.SmileyFill,
    };

    private static string ReactionColor(string id) => id switch
    {
        "react-like" => FgBlue,
        "react-love" => FgPink,
        "react-applause" => FgAmber,
        "react-laugh" => FgYellow,
        "react-surprised" => FgYellow,
        _ => FgWhite,
    };

    /// <summary>Phosphor Icons path data (256×256 viewBox, raw SVG "d"), MIT licensed.</summary>
    private static class Paths
    {
        public const string MicrophoneFill = "M80,128V64a48,48,0,0,1,96,0v64a48,48,0,0,1-96,0Zm128,0a8,8,0,0,0-16,0,64,64,0,0,1-128,0,8,8,0,0,0-16,0,80.11,80.11,0,0,0,72,79.6V240a8,8,0,0,0,16,0V207.6A80.11,80.11,0,0,0,208,128Z";
        public const string MicrophoneSlash = "M213.92,218.62l-160-176A8,8,0,0,0,42.08,53.38L80,95.09V128a48,48,0,0,0,69.11,43.12l11.1,12.2A63.41,63.41,0,0,1,128,192a64.07,64.07,0,0,1-64-64,8,8,0,0,0-16,0,80.11,80.11,0,0,0,72,79.6V240a8,8,0,0,0,16,0V207.59a78.83,78.83,0,0,0,35.16-12.22l30.92,34a8,8,0,1,0,11.84-10.76ZM128,160a32,32,0,0,1-32-32V112.69l41.66,45.82A32,32,0,0,1,128,160Zm57.52-3.91A63.32,63.32,0,0,0,192,128a8,8,0,0,1,16,0,79.16,79.16,0,0,1-8.11,35.12,8,8,0,0,1-7.19,4.49,7.88,7.88,0,0,1-3.51-.82A8,8,0,0,1,185.52,156.09ZM84,44.87A48,48,0,0,1,176,64v64a49.19,49.19,0,0,1-.26,5,8,8,0,0,1-8,7.17,8.13,8.13,0,0,1-.84,0,8,8,0,0,1-7.12-8.79c.11-1.1.17-2.24.17-3.36V64A32,32,0,0,0,98.64,51.25,8,8,0,1,1,84,44.87Z";
        public const string VideoCameraFill = "M192,72V184a16,16,0,0,1-16,16H32a16,16,0,0,1-16-16V72A16,16,0,0,1,32,56H176A16,16,0,0,1,192,72Zm58,.25a8.23,8.23,0,0,0-6.63,1.22L209.78,95.86A4,4,0,0,0,208,99.19v57.62a4,4,0,0,0,1.78,3.33l33.78,22.52a8,8,0,0,0,8.58.19,8.33,8.33,0,0,0,3.86-7.17V80A8,8,0,0,0,250,72.25Z";
        public const string VideoCameraSlash = "M251.77,73a8,8,0,0,0-8.21.39L208,97.05V72a16,16,0,0,0-16-16H113.06a8,8,0,0,0,0,16H192v87.63a8,8,0,0,0,16,0V159l35.56,23.71A8,8,0,0,0,248,184a8,8,0,0,0,8-8V80A8,8,0,0,0,251.77,73ZM240,161.05l-32-21.33V116.28L240,95ZM53.92,34.62A8,8,0,1,0,42.08,45.38L51.73,56H32A16,16,0,0,0,16,72V184a16,16,0,0,0,16,16H182.64l19.44,21.38a8,8,0,1,0,11.84-10.76ZM32,184V72H66.28L168.1,184Z";
        public const string HandFill = "M219.31,98.46A88,88,0,1,1,67.08,186.77h0L26.15,115.88a16,16,0,0,1,27.69-16L72.4,132a8,8,0,0,0,13.86-8L47,56A16,16,0,0,1,74.69,40L114,108a8,8,0,1,0,13.85-8l-30-52a16,16,0,0,1,27.71-16L166,102.12A48.25,48.25,0,0,0,152,136a47.59,47.59,0,0,0,9.6,28.8,8,8,0,1,0,12.79-9.61A32,32,0,0,1,181,110.26a8,8,0,0,0,2.17-10.43L171.71,80a16,16,0,0,1,27.71-16l19.89,34.46Zm-29.37-57A43.74,43.74,0,0,1,216.74,62l.33.57a8,8,0,0,0,13.86-8L230.6,54a59.64,59.64,0,0,0-36.54-28,8,8,0,0,0-4.12,15.46ZM79.58,225.72A103.58,103.58,0,0,1,53.93,196a8,8,0,0,0-13.86,8,119.56,119.56,0,0,0,29.6,34.28,8,8,0,0,0,9.91-12.56Z";
        public const string HandOutline = "M220.17,100,202.86,70a28,28,0,0,0-38.24-10.25,27.69,27.69,0,0,0-9,8.34L138.2,38a28,28,0,0,0-48.48,0A28,28,0,0,0,48.15,74l1.59,2.76A27.67,27.67,0,0,0,38,80.41a28,28,0,0,0-10.24,38.25l40,69.32a87.47,87.47,0,0,0,53.43,41,88.56,88.56,0,0,0,22.92,3,88,88,0,0,0,76.06-132Zm-6.66,62.64A72,72,0,0,1,81.62,180l-40-69.32a12,12,0,0,1,20.78-12L81.63,132a8,8,0,1,0,13.85-8L62,66A12,12,0,1,1,82.78,54L114,108a8,8,0,1,0,13.85-8L103.57,58h0a12,12,0,1,1,20.78-12l33.42,57.9a48,48,0,0,0-5.54,60.6,8,8,0,0,0,13.24-9A32,32,0,0,1,172.78,112a8,8,0,0,0,2.13-10.4L168.23,90A12,12,0,1,1,189,78l17.31,30A71.56,71.56,0,0,1,213.51,162.62ZM184.25,31.71A8,8,0,0,1,194,26a59.62,59.62,0,0,1,36.53,28l.33.57a8,8,0,1,1-13.85,8l-.33-.57a43.67,43.67,0,0,0-26.8-20.5A8,8,0,0,1,184.25,31.71ZM80.89,237a8,8,0,0,1-11.23,1.33A119.56,119.56,0,0,1,40.06,204a8,8,0,0,1,13.86-8,103.67,103.67,0,0,0,25.64,29.72A8,8,0,0,1,80.89,237Z";
        public const string MonitorFill = "M168,224a8,8,0,0,1-8,8H96a8,8,0,0,1,0-16h64A8,8,0,0,1,168,224ZM232,64V176a24,24,0,0,1-24,24H48a24,24,0,0,1-24-24V64A24,24,0,0,1,48,40H208A24,24,0,0,1,232,64Zm-74.34,42.34-24-24a8,8,0,0,0-11.32,0l-24,24a8,8,0,0,0,11.32,11.32L120,107.31V152a8,8,0,0,0,16,0V107.31l10.34,10.35a8,8,0,0,0,11.32-11.32Z";
        public const string MonitorOutline = "M208,40H48A24,24,0,0,0,24,64V176a24,24,0,0,0,24,24H208a24,24,0,0,0,24-24V64A24,24,0,0,0,208,40Zm8,136a8,8,0,0,1-8,8H48a8,8,0,0,1-8-8V64a8,8,0,0,1,8-8H208a8,8,0,0,1,8,8Zm-48,48a8,8,0,0,1-8,8H96a8,8,0,0,1,0-16h64A8,8,0,0,1,168,224ZM157.66,106.34a8,8,0,0,1-11.32,11.32L136,107.31V152a8,8,0,0,1-16,0V107.31l-10.34,10.35a8,8,0,0,1-11.32-11.32l24-24a8,8,0,0,1,11.32,0Z";
        public const string PhoneDisconnect = "M236.28,161.84a16,16,0,0,1-18.38,5.06l-49-17.39-.29-.11a16,16,0,0,1-9.72-11.59l-6.21-29.75h0a76.52,76.52,0,0,0-49.68.11l-5.9,29.52a16,16,0,0,1-9.75,11.73l-.29.11-49,17.37A15.8,15.8,0,0,1,32.35,168a16,16,0,0,1-12.63-6.14c-17.23-22.22-15.3-51.71,4.69-71.71,56.15-56.17,151-56.17,207.18,0h0C251.58,110.13,253.51,139.62,236.28,161.84ZM216,192H40a8,8,0,0,0,0,16H216a8,8,0,0,0,0-16Z";
        public const string ThumbsUpFill = "M234,80.12A24,24,0,0,0,216,72H160V56a40,40,0,0,0-40-40,8,8,0,0,0-7.16,4.42L75.06,96H32a16,16,0,0,0-16,16v88a16,16,0,0,0,16,16H204a24,24,0,0,0,23.82-21l12-96A24,24,0,0,0,234,80.12ZM32,112H72v88H32Z";
        public const string HeartFill = "M240,102c0,70-103.79,126.66-108.21,129a8,8,0,0,1-7.58,0C119.79,228.66,16,172,16,102A62.07,62.07,0,0,1,78,40c20.65,0,38.73,8.88,50,23.89C139.27,48.88,157.35,40,178,40A62.07,62.07,0,0,1,240,102Z";
        public const string HandsClappingFill = "M188.87,65A18,18,0,0,0,157.62,83L133.36,41a18,18,0,0,0-31.22,18L96.4,49A18,18,0,0,0,65.18,67l3.34,5.77A26,26,0,0,0,39.74,111l3,5.2A26,26,0,0,0,23.5,155l35.27,61a80.14,80.14,0,0,0,149.52-39.57A71.92,71.92,0,0,0,210,101.58Zm1.2,127.56A64.12,64.12,0,0,1,72.65,208L37.38,147a10,10,0,0,1,17.34-10L75,172a8,8,0,0,0,13.87-8L53.62,103A10,10,0,0,1,71,93l31.81,55a8,8,0,0,0,13.87-8l-26-45a10,10,0,0,1,17.35-10l36.5,63a8,8,0,0,0,13.87-8l-12.6-21.75A10,10,0,0,1,163.44,109l20.22,35A63.52,63.52,0,0,1,190.07,192.57ZM160.22,24V8a8,8,0,0,1,16,0V24a8,8,0,0,1-16,0Zm33.22,6,8-13.1a8,8,0,0,1,13.68,8.33l-8,13.11a8,8,0,0,1-6.84,3.83A8,8,0,0,1,193.44,30Zm45,33.66-15.05,4.85a8.15,8.15,0,0,1-2.46.39,8,8,0,0,1-2.46-15.62l15.06-4.85a8,8,0,1,1,4.91,15.23Z";
        public const string SmileyFill = "M128,24A104,104,0,1,0,232,128,104.11,104.11,0,0,0,128,24ZM92,96a12,12,0,1,1-12,12A12,12,0,0,1,92,96Zm82.92,60c-10.29,17.79-27.39,28-46.92,28s-36.63-10.2-46.92-28a8,8,0,1,1,13.84-8c7.47,12.91,19.21,20,33.08,20s25.61-7.1,33.08-20a8,8,0,1,1,13.84,8ZM164,120a12,12,0,1,1,12-12A12,12,0,0,1,164,120Z";
        // Open-mouth "wow" face: round eyes + a round open mouth (holes wound opposite the face).
        public const string SmileyOpenFill = "M128,24A104,104,0,1,0,232,128,104.11,104.11,0,0,0,128,24ZM92,91a13,13,0,1,1-13,13A13,13,0,0,1,92,91ZM164,91a13,13,0,1,1-13,13A13,13,0,0,1,164,91ZM128,146a22,22,0,1,1-22,22A22,22,0,0,1,128,146Z";
    }
}
