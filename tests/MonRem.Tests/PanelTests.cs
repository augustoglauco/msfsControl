using MonRem.Client.Core.Panels;
using MonRem.Host.Core.Panels;
using MonRem.Host.Core.Sim;
using MonRem.Protocol.Panels;
using MonRem.Protocol.V1;

namespace MonRem.Tests;

public class PanelTests
{
    internal const string FcuLayoutJson = """
        {
          // comentários e vírgula sobrando são aceitos
          "id": "a320",
          "name": "A320 FCU",
          "aircraft": "A320|A20N",
          "panels": [
            {
              "id": "fcu",
              "name": "FCU",
              "width": 1000, "height": 400,
              "widgets": [
                { "type": "button", "id": "ap1", "label": "AP1", "x": 10, "y": 10, "w": 100, "h": 60,
                  "press": { "fire": "K:AP_MASTER" },
                  "source": "L:AP1_ACTIVE", "when": "> 0.5" },
                { "type": "display", "id": "alt", "x": 120, "y": 10, "w": 200, "h": 60,
                  "source": "A:AUTOPILOT ALTITUDE LOCK VAR,feet", "format": "00000", "epsilon": 1 },
                { "type": "encoder", "id": "hdg", "label": "HDG", "x": 330, "y": 10, "w": 120, "h": 120,
                  "inc": { "fire": "K:HEADING_BUG_INC" }, "dec": { "fire": "K:HEADING_BUG_DEC" },
                  "push": { "rpn": "(>H:A320_FCU_HDG_PUSH)" } },
                { "type": "toggle", "id": "bat", "label": "BAT 1", "x": 460, "y": 10, "w": 100, "h": 60,
                  "source": "B:ELECTRICAL_Battery_1",
                  "on": { "set": "B:ELECTRICAL_Battery_1", "value": 1 }, "off": { "set": "B:ELECTRICAL_Battery_1", "value": 0 } },
                { "type": "selector", "id": "nd", "label": "ND MODE", "x": 570, "y": 10, "w": 200, "h": 80,
                  "source": "L:ND_MODE",
                  "positions": [
                    { "label": "LS",  "value": 0, "action": { "set": "L:ND_MODE", "value": 0 } },
                    { "label": "VOR", "value": 1, "action": { "set": "L:ND_MODE", "value": 1 } },
                    { "label": "NAV", "value": 2, "action": { "set": "L:ND_MODE", "value": 2 } },
                  ] },
                { "type": "gauge", "id": "n1", "label": "N1", "x": 780, "y": 10, "w": 200, "h": 200,
                  "source": "A:TURB ENG N1:1,percent", "min": 0, "max": 110, "unit": "%" },
                { "type": "annunciator", "x": 10, "y": 300, "w": 100, "h": 40, "label": "MASTER WARN",
                  "source": "L:MASTER_WARNING", "color": "#FF3B30" },
                { "type": "label", "x": 120, "y": 300, "w": 200, "h": 40, "label": "FCU" },
                // inválido: tipo desconhecido e ação com dois alvos
                { "type": "slider", "x": 0, "y": 0, "w": 10, "h": 10 },
                { "type": "button", "id": "bad", "x": 10, "y": 200, "w": 50, "h": 50,
                  "press": { "fire": "K:X", "rpn": "1" } },
              ],
            },
          ],
        }
        """;

    [Fact]
    public void Parses_layout_and_reports_only_real_problems()
    {
        var layout = PanelJson.ParseLayout(FcuLayoutJson);
        Assert.Equal("a320", layout.Id);
        var panel = Assert.Single(layout.Panels);
        Assert.Equal(10, panel.Widgets.Count);

        var problems = PanelJson.Validate(layout);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("slider", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("exatamente um", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "fire": "A:FOO" }""", "fire aceita")]
    [InlineData("""{ "set": "K:FOO", "value": 1 }""", "set aceita")]
    [InlineData("""{ "set": "L:FOO" }""", "precisa de value")]
    [InlineData("""{ "fire": "K:FOO", "args": [1,2,3,4,5,6] }""", "5 args")]
    [InlineData("""{ }""", "exatamente um")]
    public void Validates_actions(string json, string expected)
    {
        var action = System.Text.Json.JsonSerializer.Deserialize<PanelAction>(json, PanelJson.Options)!;
        Assert.Contains(expected, PanelJson.ValidateAction(action), StringComparison.Ordinal);
    }

    [Fact]
    public void Widget_outside_panel_and_bad_condition_are_flagged()
    {
        var panel = new PanelModel { Id = "p", Width = 100, Height = 100 };
        Assert.Contains(PanelJson.ValidateWidget(panel, new WidgetModel { Type = "label", X = 90, Y = 0, W = 50, H = 10 }), p => p.Contains("fora", StringComparison.Ordinal));
        Assert.Contains(PanelJson.ValidateWidget(panel, new WidgetModel { Type = "annunciator", Source = "L:X", When = "about 3" }), p => p.Contains("condição", StringComparison.Ordinal));
        Assert.Contains(PanelJson.ValidateWidget(panel, new WidgetModel { Type = "display", Source = "K:X" }), p => p.Contains("legível", StringComparison.Ordinal));
    }

    [Fact]
    public void Layout_store_matches_by_aircraft_specific_first_then_priority()
    {
        var store = new LayoutStore();
        store.Load(
        [
            new LayoutFile { Id = "generic", Panels = [new PanelModel { Id = "p" }] },
            new LayoutFile { Id = "a320-low", Aircraft = "A320", Priority = 1 },
            new LayoutFile { Id = "a320-high", Aircraft = "A320", Priority = 5 },
            new LayoutFile { Id = "b737", Aircraft = "737" },
        ]);

        Assert.Equal("a320-high", store.Match(new AircraftInfo("FlyByWire A320neo", "", "", "", ""))?.Id);
        Assert.Equal("b737", store.Match(new AircraftInfo("PMDG", "", "", "B737", ""))?.Id);
        Assert.Equal("generic", store.Match(new AircraftInfo("Cessna 172", "", "", "", ""))?.Id);
        Assert.Equal("generic", store.Match(null)?.Id);
        Assert.NotNull(store.FindPanel("generic/p"));
        Assert.Null(store.FindPanel("generic/x"));
        Assert.Null(store.FindPanel("semBarra"));
    }

    [Fact]
    public void Layout_store_reload_reads_folder_and_skips_broken_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monrem-layouts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a320.json"), FcuLayoutJson);
            File.WriteAllText(Path.Combine(dir, "quebrado.json"), "{ isto não é json");
            File.WriteAllText(Path.Combine(dir, "semid.json"), """{ "panels": [] }""");
            var store = new LayoutStore();
            var changed = 0;
            store.Changed += () => changed++;
            store.Reload(dir);

            Assert.Equal(1, changed);
            Assert.Equal(["a320", "semid"], store.Layouts.Select(l => l.Id).Order().ToArray());
            Assert.Equal(["a320/fcu"], store.Find("a320")!.QualifiedPanelIds.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Runtime_subscribes_each_source_once_and_reflects_values()
    {
        var link = new FakeLink();
        var panel = PanelJson.ParseLayout(FcuLayoutJson).Panels[0];
        await using var rt = new PanelRuntime(panel, link);
        await rt.StartAsync(CancellationToken.None);

        var sub = Assert.Single(link.Subscriptions);
        Assert.Equal(6, sub.Vars.Count); // ap1, alt, bat, nd, n1, master warn (widgets inválidos não assinam)
        Assert.Equal(1, sub.Vars.Single(v => v.Ref.StartsWith("A:AUTOPILOT", StringComparison.Ordinal)).Epsilon);

        var ap1 = W(rt, "ap1");
        var alt = W(rt, "alt");
        var nd = W(rt, "nd");
        var n1 = W(rt, "n1");
        Assert.Equal("---", rt.DisplayText(alt));
        Assert.False(rt.IsLit(ap1));

        link.Push(sub, ("L:AP1_ACTIVE", 1), ("A:AUTOPILOT ALTITUDE LOCK VAR,feet", 3500), ("L:ND_MODE", 1.9), ("A:TURB ENG N1:1,percent", 55));
        Assert.True(rt.IsLit(ap1));
        Assert.Equal("03500", rt.DisplayText(alt));
        Assert.Equal(2, rt.SelectorIndex(nd));
        Assert.Equal(0.5, rt.GaugeFraction(n1)!.Value, 3);

        // SimValues de outra assinatura é ignorado.
        link.Raise(new SimValues { SubId = 999, Items = { new SimValue { Handle = 1, Number = 0 } } });
        Assert.True(rt.IsLit(ap1));
    }

    [Fact]
    public async Task Runtime_actions_become_protocol_messages()
    {
        var link = new FakeLink();
        var panel = PanelJson.ParseLayout(FcuLayoutJson).Panels[0];
        await using var rt = new PanelRuntime(panel, link);

        await rt.PressAsync(W(rt, "ap1"));
        await rt.RotateAsync(W(rt, "hdg"), -3);
        await rt.RotateAsync(W(rt, "hdg"), 500); // limitado a 20 por gesto
        await rt.PushAsync(W(rt, "hdg"));
        await rt.SetToggleAsync(W(rt, "bat"), on: true);
        await rt.SelectAsync(W(rt, "nd"), 2);
        await rt.PressAsync(W(rt, "bad"));       // inválido: não faz nada

        Assert.Equal("K:AP_MASTER", link.Fired[0].Ref);
        Assert.Equal(3, link.Fired.Count(f => f.Ref == "K:HEADING_BUG_DEC"));
        Assert.Equal(PanelRuntime.MaxDetentsPerGesture, link.Fired.Count(f => f.Ref == "K:HEADING_BUG_INC"));
        Assert.Contains(link.Fired, f => f.Ref == "RPN:(>H:A320_FCU_HDG_PUSH)");
        Assert.Contains(link.Set, s => s.Ref == "B:ELECTRICAL_Battery_1" && s.Number == 1);
        Assert.Contains(link.Set, s => s.Ref == "L:ND_MODE" && s.Number == 2);
        Assert.DoesNotContain(link.Fired, f => f.Ref == "K:X");
    }

    [Fact]
    public async Task Runtime_reports_failed_actions_and_subscription_errors()
    {
        var link = new FakeLink { FailWith = new Error { Code = ErrorCode.ErrorWasmBridgeMissing, Message = "sem WASM" }, RejectSource = "L:MASTER_WARNING" };
        var panel = PanelJson.ParseLayout(FcuLayoutJson).Panels[0];
        await using var rt = new PanelRuntime(panel, link);
        await rt.StartAsync(CancellationToken.None);
        Assert.True(rt.SourceErrors.ContainsKey("L:MASTER_WARNING"));

        List<string> failures = [];
        rt.ActionFailed += failures.Add;
        await rt.PushAsync(W(rt, "hdg"));
        Assert.Contains("ErrorWasmBridgeMissing", Assert.Single(failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Example_layout_in_docs_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "exemplos", "layout-a320.json");
        var layout = PanelJson.ParseLayout(File.ReadAllText(path));
        Assert.Empty(PanelJson.Validate(layout));
        // O exemplo só pode usar o que o host suporta sem módulo WASM (A:/K:/B:, não H:/RPN:).
        var refs = layout.Panels.SelectMany(p => p.Widgets).SelectMany(w => new[] { w.Source }
            .Concat(new[] { w.Press, w.PressOff, w.Release, w.On, w.Off, w.Inc, w.Dec, w.Push }.Concat(w.Positions.Select(x => x.Action))
                .Where(a => a is not null).Select(a => a!.Fire ?? a.Set ?? (a.Rpn is null ? null : "RPN:"))))
            .Where(r => r is not null).ToList();
        Assert.All(refs, r => Assert.True(
            r!.StartsWith("A:", StringComparison.Ordinal) || r.StartsWith("K:", StringComparison.Ordinal) || r.StartsWith("B:", StringComparison.Ordinal),
            r));
    }

    [Fact]
    public async Task Press_off_is_used_when_button_is_lit()
    {
        var panel = new PanelModel
        {
            Id = "p",
            Widgets =
            [
                new WidgetModel
                {
                    Type = "button", Id = "ap", X = 0, Y = 0, W = 100, H = 50, Source = "A:AUTOPILOT MASTER,bool",
                    Press = new PanelAction { Fire = "K:AUTOPILOT_ON" }, PressOff = new PanelAction { Fire = "K:AUTOPILOT_OFF" },
                },
            ],
        };
        var link = new FakeLink();
        await using var rt = new PanelRuntime(panel, link);
        await rt.StartAsync(CancellationToken.None);
        var ap = rt.Widgets[0];

        await rt.PressAsync(ap);
        link.Push(link.Subscriptions[0], ("A:AUTOPILOT MASTER,bool", 1));
        await rt.PressAsync(ap);
        link.Push(link.Subscriptions[0], ("A:AUTOPILOT MASTER,bool", 0));
        await rt.PressAsync(ap);

        Assert.Equal(["K:AUTOPILOT_ON", "K:AUTOPILOT_OFF", "K:AUTOPILOT_ON"], link.Fired.Select(f => f.Ref).ToArray());
        Assert.Contains(PanelJson.ValidateWidget(panel, panel.Widgets[0] with { Source = null }), p => p.Contains("press_off", StringComparison.Ordinal));
    }

    [Fact]
    public void Shared_value_rules_match_esp32_semantics()
    {
        Assert.True(Comparison.TryParse(">= 2", out var c));
        Assert.True(c!.Test(2));
        Assert.False(Comparison.TryParse("~ 2", out _));
        Assert.True(Comparison.IsLit(null, 0.1));
        Assert.Equal("HDG 090", ValueFormat.Format("HDG {0:000}", 90, null));
        Assert.Equal("ERR", ValueFormat.Format("{0:", 1, null));
        Assert.Equal("texto", ValueFormat.Format("00", null, "texto"));
    }

    private static WidgetState W(PanelRuntime rt, string id) => rt.Widgets.Single(w => w.Model.Id == id);

    internal sealed class FakeLink : IPanelSimLink
    {
        public List<SimSubscribe> Subscriptions { get; } = [];
        public List<SimFireEvent> Fired { get; } = [];
        public List<SimSetVar> Set { get; } = [];
        public Error? FailWith { get; init; }
        public string? RejectSource { get; init; }

        public event Action<SimValues>? Values;

        public Task<SimSubscribed> SubscribeAsync(SimSubscribe request, CancellationToken ct)
        {
            Subscriptions.Add(request);
            var r = new SimSubscribed { SubId = request.SubId };
            for (var i = 0; i < request.Vars.Count; i++)
            {
                r.Handles.Add(request.Vars[i].Ref == RejectSource
                    ? new SimHandle { Error = new Error { Code = ErrorCode.ErrorInvalidArgument, Message = "não existe" } }
                    : new SimHandle { Handle = (uint)(i + 1) });
            }
            return Task.FromResult(r);
        }

        public Task UnsubscribeAsync(uint subId, CancellationToken ct) => Task.CompletedTask;

        public Task<SimAck> FireAsync(SimFireEvent request, CancellationToken ct)
        {
            Fired.Add(request);
            return Task.FromResult(Ack());
        }

        public Task<SimAck> SetAsync(SimSetVar request, CancellationToken ct)
        {
            Set.Add(request);
            return Task.FromResult(Ack());
        }

        public void Push(SimSubscribe sub, params (string Ref, double Value)[] values)
        {
            var msg = new SimValues { SubId = sub.SubId };
            foreach (var (r, v) in values)
                msg.Items.Add(new SimValue { Handle = (uint)(sub.Vars.ToList().FindIndex(x => x.Ref == r) + 1), Number = v });
            Raise(msg);
        }

        public void Raise(SimValues values) => Values?.Invoke(values);

        private SimAck Ack() => FailWith is null ? new SimAck { Ok = true } : new SimAck { Ok = false, Error = FailWith };
    }
}
