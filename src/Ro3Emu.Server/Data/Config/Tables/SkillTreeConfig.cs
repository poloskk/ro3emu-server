using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SkillTreeConfig (data/config/SkillTreeConfig.json).</summary>
[ConfigTable("SkillTreeConfig")]
public readonly partial record struct SkillTreeConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJob")] public partial long Job { get; }
    [ConfigField("_iY")] public partial long Y { get; }
    [ConfigField("_iX1")] public partial long X1 { get; }
    [ConfigField("_iX2")] public partial long X2 { get; }
    [ConfigField("_iX3")] public partial long X3 { get; }
    [ConfigField("_iX4")] public partial long X4 { get; }
    [ConfigField("_iX5")] public partial long X5 { get; }
    [ConfigField("_iX6")] public partial long X6 { get; }
    [ConfigField("_iX7")] public partial long X7 { get; }

    /// <summary>The row's skill groups, _iX1 to _iX7 (0 = an empty cell).</summary>
    public long[] Groups => [X1, X2, X3, X4, X5, X6, X7];
}
