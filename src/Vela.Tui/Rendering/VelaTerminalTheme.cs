using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Vela.Tui.Application;
using TAttribute = Terminal.Gui.Drawing.Attribute;

namespace Vela.Tui.Rendering;

public static class VelaTerminalTheme
{
    public const string Base = "Vela.Base";
    public const string Shell = "Vela.Shell";
    public const string Header = "Vela.Header";
    public const string Heading = "Vela.Heading";
    public const string Panel = "Vela.Panel";
    public const string Navigation = "Vela.Navigation";
    public const string Footer = "Vela.Footer";
    public const string Badge = "Vela.Badge";
    public const string TableHeader = "Vela.TableHeader";
    public const string Divider = "Vela.Divider";
    public const string Success = "Vela.Success";
    public const string SuccessStrong = "Vela.SuccessStrong";
    public const string Attention = "Vela.Attention";
    public const string AttentionStrong = "Vela.AttentionStrong";
    public const string Error = "Vela.Error";
    public const string ErrorStrong = "Vela.ErrorStrong";
    public const string Muted = "Vela.Muted";
    public const string Surface = "Vela.Surface";
    public const string SurfacePanel = "Vela.SurfacePanel";
    public const string InfoPanel = "Vela.InfoPanel";
    public const string SuccessPanel = "Vela.SuccessPanel";
    public const string AttentionPanel = "Vela.AttentionPanel";
    public const string ErrorPanel = "Vela.ErrorPanel";
    public const string Input = "Vela.Input";
    public const string Info = "Vela.Info";
    public const string InfoStrong = "Vela.InfoStrong";
    public const string ActionBar = "Vela.ActionBar";
    public const string Selection = "Vela.Selection";
    public const string LogPanel = "Vela.LogPanel";

    private static readonly Color Canvas = new("#0D1117");
    private static readonly Color PanelSurface = new("#161B22");
    private static readonly Color Border = new("#30363D");
    private static readonly Color PrimaryText = new("#C9D1D9");
    private static readonly Color SteelBlue = new("#58A6FF");
    private static readonly Color ForestGreen = new("#3FB950");
    private static readonly Color Amber = new("#D29922");
    private static readonly Color Coral = new("#F85149");
    private static readonly Color BlueGrey = new("#8B949E");
    private static readonly Color BorderSoft = new("#29313A");
    private static readonly Color BorderStrong = new("#3B4654");
    private static readonly Color DeepInk = new("#0D1117");
    private static readonly Color ActionBarText = new("#C9D1D9");
    private static readonly Color ActionBarSurface = new("#161B22");
    private static readonly Color InfoSurface = new("#111B27");
    private static readonly Color SuccessSurface = new("#122117");
    private static readonly Color AttentionSurface = new("#241C0E");
    private static readonly Color ErrorSurface = new("#271313");
    private static readonly Color SelectionSurface = new("#1B2633");
    private static readonly Color LogSurface = new("#050505");

    public static void Register()
    {
        Add(Base, new Scheme
        {
            Normal = new TAttribute(PrimaryText, Color.None),
            Focus = new TAttribute(Color.White, Color.None, TextStyle.Bold)
        });
        Add(Shell, new Scheme
        {
            Normal = new TAttribute(PrimaryText, Canvas),
            Focus = new TAttribute(Color.White, Canvas, TextStyle.Bold)
        });
        Add(Header, new Scheme
        {
            Normal = new TAttribute(PrimaryText, Canvas, TextStyle.Bold),
            Focus = new TAttribute(Color.White, Canvas, TextStyle.Bold)
        });
        Add(Heading, new Scheme
        {
            Normal = new TAttribute(SteelBlue, Canvas, TextStyle.Bold),
            Focus = new TAttribute(Color.White, Canvas, TextStyle.Bold)
        });
        Add(Panel, new Scheme
        {
            Normal = new TAttribute(Border, Canvas),
            Focus = new TAttribute(SteelBlue, Canvas, TextStyle.Bold)
        });
        Add(Navigation, new Scheme
        {
            Normal = new TAttribute(BlueGrey, Canvas),
            Focus = new TAttribute(Color.White, SelectionSurface, TextStyle.Bold),
            Disabled = new TAttribute(BlueGrey, Canvas)
        });
        Add(Footer, new Scheme
        {
            Normal = new TAttribute(ActionBarText, ActionBarSurface),
            Focus = new TAttribute(Color.White, ActionBarSurface, TextStyle.Bold)
        });
        Add(Badge, new Scheme
        {
            Normal = new TAttribute(SteelBlue, PanelSurface, TextStyle.Bold),
            Focus = new TAttribute(Color.White, PanelSurface, TextStyle.Bold)
        });
        Add(TableHeader, new Scheme
        {
            Normal = new TAttribute(SteelBlue, PanelSurface, TextStyle.Bold),
            Focus = new TAttribute(Color.White, PanelSurface, TextStyle.Bold)
        });
        Add(Divider, new Scheme
        {
            Normal = new TAttribute(BorderSoft, Canvas),
            Focus = new TAttribute(BorderSoft, Canvas)
        });
        Add(Success, Semantic(ForestGreen));
        Add(SuccessStrong, SemanticStrong(ForestGreen));
        Add(Info, Semantic(SteelBlue));
        Add(InfoStrong, SemanticStrong(SteelBlue));
        Add(Attention, Semantic(Amber));
        Add(AttentionStrong, SemanticStrong(Amber));
        Add(Error, Semantic(Coral));
        Add(ErrorStrong, SemanticStrong(Coral));
        Add(Muted, Semantic(BlueGrey));
        Add(Surface, new Scheme
        {
            Normal = new TAttribute(PrimaryText, PanelSurface),
            Focus = new TAttribute(Color.White, PanelSurface, TextStyle.Bold)
        });
        Add(SurfacePanel, new Scheme
        {
            Normal = new TAttribute(BorderStrong, PanelSurface),
            Focus = new TAttribute(Color.White, PanelSurface, TextStyle.Bold)
        });
        Add(InfoPanel, new Scheme
        {
            Normal = new TAttribute(SteelBlue, InfoSurface),
            Focus = new TAttribute(Color.White, InfoSurface, TextStyle.Bold)
        });
        Add(SuccessPanel, new Scheme
        {
            Normal = new TAttribute(ForestGreen, SuccessSurface),
            Focus = new TAttribute(Color.White, SuccessSurface, TextStyle.Bold)
        });
        Add(AttentionPanel, new Scheme
        {
            Normal = new TAttribute(Amber, AttentionSurface),
            Focus = new TAttribute(Color.White, AttentionSurface, TextStyle.Bold)
        });
        Add(ErrorPanel, new Scheme
        {
            Normal = new TAttribute(Coral, ErrorSurface),
            Focus = new TAttribute(Color.White, ErrorSurface, TextStyle.Bold)
        });
        Add(Input, new Scheme
        {
            Normal = new TAttribute(Amber, Color.None),
            Focus = new TAttribute(DeepInk, Amber, TextStyle.Bold)
        });
        Add(ActionBar, new Scheme
        {
            Normal = new TAttribute(ActionBarText, ActionBarSurface),
            Focus = new TAttribute(Color.White, ActionBarSurface, TextStyle.Bold)
        });
        Add(Selection, new Scheme
        {
            Normal = new TAttribute(SteelBlue, SelectionSurface, TextStyle.Bold),
            Focus = new TAttribute(Color.White, SelectionSurface, TextStyle.Bold)
        });
        Add(LogPanel, new Scheme
        {
            Normal = new TAttribute(PrimaryText, LogSurface),
            Focus = new TAttribute(Color.White, LogSurface, TextStyle.Bold)
        });
    }

    private static Scheme Semantic(Color color) => new()
    {
        Normal = new TAttribute(color, Color.None),
        Focus = new TAttribute(color, Color.None, TextStyle.Bold)
    };

    private static Scheme SemanticStrong(Color color) => new()
    {
        Normal = new TAttribute(color, Color.None, TextStyle.Bold),
        Focus = new TAttribute(color, Color.None, TextStyle.Bold)
    };

    private static void Add(string name, Scheme scheme)
    {
        if (!SchemeManager.TryGetScheme(name, out _))
        {
            SchemeManager.AddScheme(name, scheme);
        }
    }

    public static string SchemeForPreflight(AutomaticPreflightStatus status) => status switch
    {
        AutomaticPreflightStatus.Ready => Success,
        AutomaticPreflightStatus.Checking => Info,
        AutomaticPreflightStatus.Attention => Attention,
        AutomaticPreflightStatus.Failed => Error,
        _ => Muted
    };

    public static TAttribute NormalAttribute(string schemeName) =>
        SchemeManager.GetScheme(schemeName).Normal;
}
