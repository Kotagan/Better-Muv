using System.Text;
using BetterMuv.BmCli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

if (args.Length == 0 ||
    args[0] is "-h" or "--help" or "help")
    return Commands.Help();

CliContext ctx;
try
{
    ctx = CliContext.Create(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

string[] pos = CliContext.Positional(args);
if (pos.Length == 0)
    return Commands.Help();

string cmd = pos[0].ToLowerInvariant();
string[] rest = pos.Skip(1).ToArray();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return cmd switch
    {
        "flows" or "flow" => Commands.Flows(ctx),
        "presets" => Commands.Presets(ctx),
        "status" => await Commands.StatusAsync(ctx, cts.Token),
        "windows" => Commands.Windows(ctx),
        "capture" or "shot" => await Commands.CaptureAsync(ctx, rest, cts.Token),
        "click" => await Commands.ClickAsync(ctx, rest, ratio: false, cts.Token),
        "click-ratio" or "clickr" => await Commands.ClickAsync(ctx, rest, ratio: true, cts.Token),
        "probe" => await Commands.ProbeAsync(ctx, rest, cts.Token),
        "match" => await Commands.MatchAsync(ctx, args, rest, cts.Token),
        "crop" => await Commands.CropAsync(ctx, args, rest, cts.Token),
        "rois" or "roi" => Commands.Rois(ctx, rest),
        "points" or "clicks" => Commands.Points(ctx),
        _ => Unknown(cmd)
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("已取消。");
    return 130;
}
catch (Exception ex)
{
    if (ctx.Json)
        ctx.WriteJson(new { error = ex.Message, type = ex.GetType().Name });
    else
        Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"未知命令: {cmd}（bm help）");
    return 1;
}
