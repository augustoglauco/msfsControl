using System.Text.Json;
using MonRem.Host.Core.Devices;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class DeviceMapperTests
{
    private static readonly Element[] Panel =
    [
        new() { Id = 1, Kind = ElementKind.Button, Name = "AP1" },
        new() { Id = 2, Kind = ElementKind.Toggle, Name = "BAT1" },
        new() { Id = 3, Kind = ElementKind.Selector, Name = "ND_MODE", Max = 2 },
        new() { Id = 4, Kind = ElementKind.Encoder, Name = "HDG", Detents = 4 },
        new() { Id = 5, Kind = ElementKind.Analog, Name = "THR1", Max = 4095 },
        new() { Id = 20, Kind = ElementKind.Led, Name = "AP1_LED" },
        new() { Id = 21, Kind = ElementKind.Segment, Name = "ALT", Digits = 5 },
        new() { Id = 22, Kind = ElementKind.Text, Name = "LCD", Rows = 2, Cols = 8 },
        new() { Id = 23, Kind = ElementKind.Stepper, Name = "N1", Max = 4000 },
        new() { Id = 24, Kind = ElementKind.Backlight, Name = "BL" },
    ];

    // Perfil escrito exatamente como o usuário escreveria no JSON.
    private const string ProfileJson = """
    {
      "name": "teste", "model": "fcu",
      "inputs": [
        { "element": "AP1", "press": { "fire": "K:AP_MASTER" } },
        { "element": "BAT1", "on": { "set": "B:ELECTRICAL_Battery_1", "value": 1 }, "off": { "set": "B:ELECTRICAL_Battery_1", "value": 0 } },
        { "element": "ND_MODE", "positions": [ { "set": "L:ND_MODE", "value": 0 }, { "set": "L:ND_MODE", "value": 1 }, { "set": "L:ND_MODE", "value": 2 } ] },
        { "element": "HDG", "inc": { "fire": "K:HEADING_BUG_INC" }, "dec": { "fire": "K:HEADING_BUG_DEC" }, "accel": { "rate": 10, "multiplier": 5 } },
        { "element": "THR1", "analog": { "set": "A:GENERAL ENG THROTTLE LEVER POSITION:1,percent", "in": [0, 4000], "out": [0, 100], "deadzone": 20 } },
        { "element": "NAO_EXISTE", "press": { "fire": "K:X" } }
      ],
      "outputs": [
        { "element": "AP1_LED", "source": "A:AUTOPILOT MASTER,bool", "when": "> 0.5" },
        { "element": "ALT", "source": "A:AUTOPILOT ALTITUDE LOCK VAR,feet", "format": "00000" },
        { "element": "LCD", "row": 1, "source": "A:AUTOPILOT HEADING LOCK DIR,degrees", "format": "HDG {0:000}" },
        { "element": "N1", "source": "A:TURB ENG N1:1,percent", "in": [0, 110], "out": [0, 4000] },
        { "element": "BL", "value": 180 }
      ]
    }
    """;

    private static DeviceMapper Mapper() =>
        new(JsonSerializer.Deserialize<DeviceProfile>(ProfileJson, DeviceProfileStore.Json)!, Panel);

    private static InputValue Pressed(uint id, bool v) => new() { Id = id, Pressed = v };
    private static InputValue Position(uint id, uint v) => new() { Id = id, Position = v };
    private static InputValue Count(uint id, int v) => new() { Id = id, Count = v };
    private static InputValue Analog(uint id, uint v) => new() { Id = id, Analog = v };

    [Fact]
    public void Reports_unknown_elements_as_warnings()
    {
        var m = Mapper();
        Assert.Contains(m.Warnings, w => w.Contains("NAO_EXISTE"));
        Assert.Equal(4, m.OutputSources.Count);
    }

    [Fact]
    public void Button_fires_on_press_only_after_baseline()
    {
        var m = Mapper();
        Assert.Empty(m.OnInput([Pressed(1, false)], 0));
        var actions = m.OnInput([Pressed(1, true)], 10);
        Assert.Equal("K:AP_MASTER", Assert.Single(actions).Ref);
        Assert.Empty(m.OnInput([Pressed(1, true)], 20)); // estado completo repetido: nada
        Assert.Empty(m.OnInput([Pressed(1, false)], 30)); // sem release configurado
    }

    [Fact]
    public void Toggle_and_selector_sync_on_connect_then_follow_changes()
    {
        var m = Mapper();
        var sync = m.OnInput([Pressed(2, true), Position(3, 2)], 0);
        Assert.Equal(2, sync.Count);
        Assert.Equal(SimDatum.Of(1), sync[0].Value);
        Assert.Equal(SimDatum.Of(2), sync[1].Value);

        Assert.Empty(m.OnInput([Pressed(2, true), Position(3, 2)], 1000));
        Assert.Equal(SimDatum.Of(0), Assert.Single(m.OnInput([Position(3, 0)], 1100)).Value);
        Assert.Equal(SimDatum.Of(0), Assert.Single(m.OnInput([Pressed(2, false)], 1200)).Value);
    }

    [Fact]
    public void Encoder_counts_detents_handles_wrap_and_accelerates()
    {
        var m = Mapper();
        Assert.Empty(m.OnInput([Count(4, 100)], 0));          // referência

        Assert.Empty(m.OnInput([Count(4, 102)], 1000));       // meio detente
        var one = m.OnInput([Count(4, 104)], 2000);           // completa 1 detente, devagar
        Assert.Equal("K:HEADING_BUG_INC", Assert.Single(one).Ref);

        var fast = m.OnInput([Count(4, 112)], 2050);          // 2 detentes em 50 ms = 40/s ≥ 10/s → ×5
        Assert.Equal(10, fast.Count);

        var back = m.OnInput([Count(4, 108)], 3050);          // −1 detente, devagar
        Assert.Equal("K:HEADING_BUG_DEC", Assert.Single(back).Ref);

        var wrap = Mapper();
        wrap.OnInput([Count(4, int.MaxValue - 1)], 0);
        Assert.Single(wrap.OnInput([Count(4, unchecked(int.MaxValue + 3))], 1000)); // passou do limite do int32
    }

    [Fact]
    public void Analog_scales_and_respects_deadzone()
    {
        var m = Mapper();
        var first = Assert.Single(m.OnInput([Analog(5, 2000)], 0));
        Assert.Equal(50, first.Value.Number);
        Assert.Empty(m.OnInput([Analog(5, 2015)], 10));       // dentro da zona morta
        Assert.Equal(100, Assert.Single(m.OnInput([Analog(5, 4095)], 20)).Value.Number); // limitado ao máximo
    }

    [Fact]
    public void Outputs_are_computed_and_only_changes_reported()
    {
        var m = Mapper();
        var led = Assert.Single(m.OnSourceValue("A:AUTOPILOT MASTER,bool", SimDatum.Of(1)));
        Assert.True(led.On);
        Assert.Empty(m.OnSourceValue("A:AUTOPILOT MASTER,bool", SimDatum.Of(1)));

        Assert.Equal("03500", Assert.Single(m.OnSourceValue("A:AUTOPILOT ALTITUDE LOCK VAR,feet", SimDatum.Of(3500))).Text);

        var lcd = Assert.Single(m.OnSourceValue("A:AUTOPILOT HEADING LOCK DIR,degrees", SimDatum.Of(45)));
        Assert.Equal("HDG 045", lcd.Text);
        Assert.Equal(1u, lcd.TextRow);

        Assert.Equal(2000, Assert.Single(m.OnSourceValue("A:TURB ENG N1:1,percent", SimDatum.Of(55))).Target);
    }

    [Fact]
    public void Full_outputs_include_constants_and_known_values()
    {
        var m = Mapper();
        m.OnSourceValue("A:AUTOPILOT MASTER,bool", SimDatum.Of(0));
        var all = m.FullOutputs();
        Assert.Contains(all, o => o.Id == 24 && o.Level == 180);
        Assert.Contains(all, o => o.Id == 20 && !o.On);
        Assert.DoesNotContain(all, o => o.Id == 21); // altitude ainda sem valor
    }

    [Fact]
    public void Text_is_truncated_to_display_columns()
    {
        var m = Mapper();
        var lcd = Assert.Single(m.OnSourceValue("A:AUTOPILOT HEADING LOCK DIR,degrees", SimDatum.Of(123456)));
        Assert.Equal(8, lcd.Text.Length);
    }

    [Fact]
    public void Profile_selection_prefers_most_specific()
    {
        var store = DeviceProfileStore.FromProfiles(
        [
            new DeviceProfile { Name = "geral", Model = "*" },
            new DeviceProfile { Name = "fcu", Model = "fcu" },
            new DeviceProfile { Name = "fcu-a320", Model = "fcu", Aircraft = "A320|A20N" },
            new DeviceProfile { Name = "meu-fcu", Model = "fcu", DeviceId = "aabbcc" },
        ]);
        var a320 = new AircraftInfo("Airbus A320neo", "", "", "A20N", "");
        var c172 = new AircraftInfo("Cessna 172", "", "", "C172", "");

        Assert.Equal("meu-fcu", store.Select("fcu", "aabbcc", a320)!.Name);
        Assert.Equal("fcu-a320", store.Select("fcu", "112233", a320)!.Name);
        Assert.Equal("fcu", store.Select("fcu", "112233", c172)!.Name);
        Assert.Equal("geral", store.Select("radio", "112233", c172)!.Name);
    }

    [Fact]
    public void Invalid_actions_become_warnings_not_crashes()
    {
        var profile = new DeviceProfile
        {
            Inputs =
            [
                new InputBinding { Element = "AP1", Press = new SimAction { Fire = "K:A", Set = "L:B" } },
                new InputBinding { Element = "BAT1", On = new SimAction { Set = "K:NOT_SETTABLE", Value = JsonDocument.Parse("1").RootElement } },
                new InputBinding { Element = "HDG", Inc = new SimAction { Fire = "nada" } },
                new InputBinding { Element = "AP1_LED", Press = new SimAction { Fire = "K:A" } },
            ],
            Outputs = [new OutputBinding { Element = "AP1_LED", Source = "A:X", When = "about 3" }],
        };
        var m = new DeviceMapper(profile, Panel);
        Assert.Equal(5, m.Warnings.Count);
        Assert.Empty(m.OnInput([Pressed(1, false), Pressed(1, true)], 0));
    }
}
