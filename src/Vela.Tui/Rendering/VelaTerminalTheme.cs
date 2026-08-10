using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Vela.Tui.Application;
using TAttribute = Terminal.Gui.Drawing.Attribute;

namespace Vela.Tui.Rendering;

public static class VelaTerminalTheme
{
    public const string Base = "Vela.Base";
    public const string Panel = "Vela.Panel";
    public const string Navigation = "Vela.Navigation";
    public const string Success = "Vela.Success";
    public const string Attention = "Vela.Attention";
    public const string Error = "Vela.Error";
    public const string Muted = "Vela.Muted";
    public const string Input = "Vela.Input";
    public const string Info = "Vela.Info";
    public const string ActionBar = "Vela.ActionBar";
    public const string Selection = "Vela.Selection";

    private static readonly Color SteelBlue = new("#7FA6C9");
    private static readonly Color ForestGreen = new("#78B88A");
    private static readonly Color Amber = new("#D2A85C");
    private static readonly Color Coral = new("#D47777");
    private static readonly Color BlueGrey = new("#7E8B98");
    private static readonly Color DeepInk = new("#14212B");
    private static readonly Color ActionBarText = new("#C4D2DD");
    private static readonly Color ActionBarSurface = new("#263541");
    private static readonly Color SelectionSurface = new("#1B2430");

    public static void Register()
    {
        Add(Base, new Scheme
        {
            Normal = new TAttribute(Color.None, Color.None),
            Focus = new TAttribute(SteelBlue, Color.None, TextStyle.Bold)
        });
        Add(Panel, new Scheme
        {
            Normal = new TAttribute(BlueGrey, Color.None),
            Focus = new TAttribute(SteelBlue, Color.None, TextStyle.Bold)
        });
        Add(Navigation, new Scheme
        {
            Normal = new TAttribute(Color.None, Color.None),
            Focus = new TAttribute(SteelBlue, Color.None, TextStyle.Bold),
            Disabled = new TAttribute(BlueGrey, Color.None)
        });
        Add(Success, Semantic(ForestGreen));
        Add(Info, Semantic(SteelBlue));
        Add(Attention, Semantic(Amber));
        Add(Error, Semantic(Coral));
        Add(Muted, Semantic(BlueGrey));
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
    }

    private static Scheme Semantic(Color color) => new()
    {
        Normal = new TAttribute(color, Color.None),
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
