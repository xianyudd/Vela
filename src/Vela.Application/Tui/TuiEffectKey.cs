namespace Vela.Application.Tui;

/// <summary>
/// Key that identifies the asynchronous effect kind a completion belongs to.
/// Used together with monotonically increasing generations to discard stale
/// results.
/// </summary>
public abstract record TuiEffectKey
{
    /// <summary>Key for startup data-root initialization.</summary>
    public sealed record Startup : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static Startup Instance { get; } = new Startup();
    }

    /// <summary>Key for read-only preflight.</summary>
    public sealed record Preflight : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static Preflight Instance { get; } = new Preflight();
    }

    /// <summary>Key for impact estimation.</summary>
    public sealed record Impact : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static Impact Instance { get; } = new Impact();
    }

    /// <summary>Key for run-history reads.</summary>
    public sealed record RunHistory : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static RunHistory Instance { get; } = new RunHistory();
    }

    /// <summary>Key for log-detail reads.</summary>
    public sealed record LogDetail : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static LogDetail Instance { get; } = new LogDetail();
    }

    /// <summary>Key for execution (single-flight).</summary>
    public sealed record Execution : TuiEffectKey
    {
        /// <summary>Singleton instance.</summary>
        public static Execution Instance { get; } = new Execution();
    }
}
