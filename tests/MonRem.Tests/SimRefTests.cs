using MonRem.Host.Core.Sim;

namespace MonRem.Tests;

public class SimRefTests
{
    [Theory]
    [InlineData("A:INDICATED ALTITUDE,feet", SimRefKind.A, "INDICATED ALTITUDE", null, "feet")]
    [InlineData("A:GENERAL ENG RPM:1,rpm", SimRefKind.A, "GENERAL ENG RPM", 1, "rpm")]
    [InlineData("a:PLANE LATITUDE", SimRefKind.A, "PLANE LATITUDE", null, null)]
    [InlineData("L:A32NX_EFIS_L_ND_MODE", SimRefKind.L, "A32NX_EFIS_L_ND_MODE", null, null)]
    [InlineData("L:XMLVAR_Baro1_Mode,number", SimRefKind.L, "XMLVAR_Baro1_Mode", null, "number")]
    [InlineData("B:ELECTRICAL_Battery_1", SimRefKind.B, "ELECTRICAL_Battery_1", null, null)]
    [InlineData("K:AP_ALT_VAR_SET_ENGLISH", SimRefKind.K, "AP_ALT_VAR_SET_ENGLISH", null, null)]
    [InlineData("H:A320_Neo_MFD_BTN_CSTR_1", SimRefKind.H, "A320_Neo_MFD_BTN_CSTR_1", null, null)]
    [InlineData("RPN:(L:X) 1 + (>L:X)", SimRefKind.Rpn, "(L:X) 1 + (>L:X)", null, null)]
    public void Parses_valid_references(string raw, SimRefKind kind, string name, int? index, string? unit)
    {
        var r = SimRef.Parse(raw);
        Assert.Equal(kind, r.Kind);
        Assert.Equal(name, r.Name);
        Assert.Equal(index, r.Index);
        Assert.Equal(unit, r.Unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("INDICATED ALTITUDE")]
    [InlineData("X:FOO")]
    [InlineData("A:")]
    [InlineData("A:ENG RPM:0")]
    [InlineData("A:ENG RPM:x")]
    [InlineData("K:FOO,bar")]
    [InlineData("RPN:")]
    public void Rejects_invalid_references(string raw) => Assert.Throws<FormatException>(() => SimRef.Parse(raw));

    [Fact]
    public void Key_ignores_case_of_A_names_and_units()
    {
        Assert.Equal(SimRef.Parse("A:Indicated Altitude,Feet").Key, SimRef.Parse("A:INDICATED ALTITUDE,feet").Key);
        Assert.NotEqual(SimRef.Parse("A:INDICATED ALTITUDE,feet").Key, SimRef.Parse("A:INDICATED ALTITUDE,meters").Key);
        Assert.Equal("GENERAL ENG RPM:2", SimRef.Parse("A:GENERAL ENG RPM:2,rpm").SimConnectName);
    }

    [Fact]
    public void Operation_capabilities_follow_kind()
    {
        Assert.True(SimRef.Parse("K:AP_MASTER").IsFireable);
        Assert.False(SimRef.Parse("K:AP_MASTER").IsReadable);
        Assert.True(SimRef.Parse("L:X").IsSettable);
        Assert.False(SimRef.Parse("H:X").IsReadable);
    }
}
