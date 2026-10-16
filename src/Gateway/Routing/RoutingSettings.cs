using Gateway.Infrastructure;

namespace Gateway.Routing;

public sealed record RoutingSettingsView(bool MaximizeUtilization, bool AutoUseResets, bool AutoUseResetsIsDefault);

public sealed record RoutingSettingsInput(bool? MaximizeUtilization, bool? AutoUseResets);

/// <summary>
/// Routing switches stored in the settings table. <c>autoUseResets</c> defaults to the value of <c>maximizeUtilization</c>
/// until it is set explicitly, so resets are only spent automatically once the user opted into maximizing utilization.
/// </summary>
public sealed class RoutingSettings(Database db)
{
    private const string MaximizeKey = "routing.maximizeUtilization";
    private const string AutoResetsKey = "routing.autoUseResets";

    public bool MaximizeUtilization => db.GetSetting(MaximizeKey) == "true";

    public bool AutoUseResets => db.GetSetting(AutoResetsKey) is { } v ? v == "true" : MaximizeUtilization;

    public RoutingSettingsView View()
    {
        var max = MaximizeUtilization;
        var auto = db.GetSetting(AutoResetsKey);
        return new RoutingSettingsView(max, auto is null ? max : auto == "true", auto is null);
    }

    public RoutingSettingsView Update(RoutingSettingsInput input)
    {
        if (input.MaximizeUtilization is { } m) db.SetSetting(MaximizeKey, m ? "true" : "false");
        if (input.AutoUseResets is { } a) db.SetSetting(AutoResetsKey, a ? "true" : "false");
        return View();
    }
}
