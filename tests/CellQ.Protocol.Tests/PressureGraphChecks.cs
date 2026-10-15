using System.Globalization;
using System.Text;
using CellQ.Protocol;

namespace CellQ.Protocol.Tests;

internal static class PressureGraphChecks
{
    internal const string Header = "TYPE,ELAPSED_MS,STATE,G1_CYCLE,G2_CYCLE,V1,V2,V3,V4,V5,V6,P1_X10_KPA,P2_X10_KPA,P3_X10_KPA,P4_X10_KPA,P5_X10_KPA,P6_X10_KPA";
    private static string Row(long time, string pressures = "258,-350,0,19,-19,123") => $"SAMPLE,{time},RUNNING,0,0,1,1,1,1,1,1,{pressures}\n";
    private static async Task<PressureLog> Read(string rows, string metadata = "PRESET_NAME,Test\nSAMPLE_INTERVAL_MS,20\n")
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("CELLQ_DATA,1\n" + metadata + Header + "\nDATA,,,,,,,,,,,,,,,,\n" + rows));
        return await PressureLogReader.ReadAsync(stream);
    }
    internal static async Task RunAllAsync(Action<bool, string> check, Func<string, Func<Task>, Task> run)
    {
        await run("Graph: regression fixture exact pressures, elapsed time and record counts", async () =>
        {
            await using var sample = typeof(PressureGraphChecks).Assembly.GetManifestResourceStream("CellQ.Tests.PressureLogFixture.csv")!;
            var data = await PressureLogReader.ReadAsync(sample);
            check(data.Count == 8027 && data.PresetName == "Heart Test" && data.TimeAt(0) == 106 && data.MaximumTimeMs == 161550, "Metadata/events are not fake readings, and the time range comes from the CSV rather than the filename.");
            check(data.PressureAt(0, 0) == 25.8 && data.PressureAt(0, 5) == 27.5 && data.MinimumPressures.All(value => value < 0), "X10 pressures divide by 10 and retain their recorded signs for all six channels.");
            check(data.MissingReadings == 0 && data.Nearest(106) == 0, "The actual sample has complete readings and accurate cursor lookup.");
        });
        await run("Graph: quoted Unicode metadata, events and missing pressure cells", async () =>
        {
            var data = await Read("EVENT,0,LOGGING_ON,,,,,,,,,,,,,,\n" + Row(100) + Row(120, "259,,1,20,-20,124"), "PRESET_NAME,\"آزمون, \"\"Heart\"\"\nTest\"\nSAMPLE_INTERVAL_MS,20\n");
            check(data.PresetName == "آزمون, \"Heart\"\nTest" && data.Count == 2, "Quoted CSV metadata is decoded without interpreting its text as instructions.");
            check(data.PressureAt(0, 1) == -35 && data.PressureAt(0, 3) == 1.9 && data.PressureAt(0, 4) == -1.9, "Positive/negative/zero readings use the correct units.");
            check(!data.HasReading(1, 1) && double.IsNaN(data.PressureAt(1, 1)) && data.MissingReadings == 1, "Missing values become gaps, never fabricated zero pressures.");
        });
        foreach (string bad in new[] { "no samples", "wrong version", "missing columns", "bad pressure", "bad time", "backward time", "malformed quote", "wrong field count" })
        {
            await run("Graph: rejects " + bad, async () =>
            {
                string file = "CELLQ_DATA,1\n" + Header + "\n" + Row(100);
                file = bad switch
                {
                    "no samples" => "CELLQ_DATA,1\n" + Header + "\n",
                    "wrong version" => file.Replace("CELLQ_DATA,1", "CELLQ_DATA,2"),
                    "missing columns" => file.Replace("P6_X10_KPA", "OTHER"),
                    "bad pressure" => file.Replace("258", "n/a"),
                    "bad time" => file.Replace("SAMPLE,100", "SAMPLE,-100"),
                    "backward time" => file + Row(99),
                    "malformed quote" => file + "SAMPLE,\"unfinished\n",
                    _ => file.Replace("258,-350", "258,7,-350")
                };
                await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(file));
                try { await PressureLogReader.ReadAsync(stream); check(false, "Invalid CSV must be refused."); }
                catch (InvalidDataException) { check(true, "Invalid input reports a useful error without invented readings."); }
            });
        }
        await run("Graph: pre-cancelled CSV loading performs no parse", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("CELLQ_DATA,1\n" + Header + "\n" + Row(100)));
            try { await PressureLogReader.ReadAsync(stream, cancelled.Token); check(false, "Cancelled load must stop."); }
            catch (OperationCanceledException) { check(true, "Local loading supports cancellation."); }
        });
        await run("Graph: viewport zoom, pan boundaries, anchored zoom and six-hour jump", () =>
        {
            var view = new GraphViewport(0, 6 * 3600000);
            check(view.Span == 30000 && view.Start == 0, "Long logs start with a readable 30-second view.");
            view.PanTo(3600000); double anchor = view.Start + view.Span * .25;
            view.SetSpan(10000, .25);
            check(Math.Abs(view.Start + view.Span * .25 - anchor) < .01, "Mouse-wheel zoom preserves the time under the pointer.");
            view.JumpTo(4 * 3600000);
            check(view.Start + view.Span / 2 == 4 * 3600000, "Jump centers on the requested elapsed hour.");
            view.PanTo(-1); check(view.Start == 0, "Dragging cannot move before recorded time.");
            view.PanTo(double.MaxValue); check(view.End == view.Maximum, "Dragging cannot move beyond recorded time.");
            view.SetZoom(100); check(view.Span == 1000, "Maximum detail is a one-second window.");
            view.ShowAll(); check(view.Start == 0 && view.End == 6 * 3600000 && view.MaximumStart == 0, "Show whole test fits the complete recording.");
            var late = new GraphViewport(7200000, 7260000); check(late.Start == 7200000, "Late-started recordings preserve elapsed time and do not open onto an empty early range.");
            return Task.CompletedTask;
        });
        await run("Graph: editable time accepts seconds or clock notation and refuses invalid times", () =>
        {
            foreach (var input in new[] { ("30", 30000d), ("01:30", 90000d), ("04:15:30.125", 15330125d) })
                check(GraphViewport.TryTime(input.Item1, out var ms) && ms == input.Item2, "Time textbox parses a supported format.");
            foreach (string input in new[] { "", "-1", "NaN", "Infinity", "1:60", "90:90", "1:2:3:4", "1.5:02" }) check(!GraphViewport.TryTime(input, out _), "Invalid time is not silently reinterpreted.");
            check(GraphViewport.FormatTime(161550) == "00:02:41.550", "Time labels retain millisecond precision.");
            return Task.CompletedTask;
        });
        await run("Graph: reduction preserves peaks/troughs and chronology", async () =>
        {
            var rows = new StringBuilder();
            for (int index = 0; index < 10000; index++) rows.Append(Row(index * 20, index == 4321 ? "950,950,950,950,950,950" : index == 7654 ? "-800,-800,-800,-800,-800,-800" : "0,0,0,0,0,0"));
            var data = await Read(rows.ToString());
            var points = PressureTraceReducer.Reduce(data, 0, 0, data.MaximumTimeMs, 200);
            check(points.Count <= 800 && points.Any(point => point.Kpa == 95) && points.Any(point => point.Kpa == -80), "Dense reduction has a bounded point count and retains narrow extrema.");
            check(points.Zip(points.Skip(1)).All(pair => pair.First.TimeMs <= pair.Second.TimeMs), "Min/max points remain in original time order.");
        });
        await run("Graph: missing readings and recording pauses break curves", async () =>
        {
            var data = await Read(Row(0) + Row(20, ",0,0,0,0,0") + Row(40) + Row(5000));
            var points = PressureTraceReducer.Reduce(data, 0, 0, 5000, 100);
            check(points.Any(point => point.TimeMs == 40 && point.BreakBefore) && points.Any(point => point.TimeMs == 5000 && point.BreakBefore), "Curves do not interpolate across missing values or long acquisition gaps.");
        });
        await run("Graph: multi-hour dense log stays within screen-column drawing budget", async () =>
        {
            var rows = new StringBuilder();
            for (int index = 0; index < 100000; index++) rows.Append(Row(index * 216L, (index % 10).ToString(CultureInfo.InvariantCulture) + ",0,0,0,0,0"));
            var data = await Read(rows.ToString(), "SAMPLE_INTERVAL_MS,216\n");
            var points = PressureTraceReducer.Reduce(data, 0, data.MinimumTimeMs, data.MaximumTimeMs, 900);
            check(data.MaximumTimeMs > 5 * 3600000 && points.Count <= 3600, "Hours of data are reduced for drawing rather than fitted as hundreds of thousands of line segments.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { PressureTraceReducer.Reduce(data, 0, 0, data.MaximumTimeMs, 900, cancelled.Token); check(false, "Cancelled render must stop."); }
            catch (OperationCanceledException) { check(true, "Obsolete long-range renders can be cancelled."); }
        });
    }
}
