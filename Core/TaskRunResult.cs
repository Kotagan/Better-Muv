namespace BetterMuv.Core;

/// <summary>单任务执行结果：供一条龙 UI 区分成功/失败（不抛异常的软失败也算失败）。</summary>
public readonly record struct TaskRunResult(bool Ok, string? Detail = null)
{
    public static TaskRunResult Success(string? detail = null) => new(true, detail);
    public static TaskRunResult Fail(string detail) => new(false, detail);
}
