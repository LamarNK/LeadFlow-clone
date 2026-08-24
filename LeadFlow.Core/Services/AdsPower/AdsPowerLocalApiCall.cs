namespace LeadFlow.Core.Services.AdsPower;

internal static class AdsPowerLocalApiCall
{
    public const string OperationBrowserStart = "browser/start";
    public const string OperationBrowserStop = "browser/stop";
    public const string OperationUserList = "user/list";
    public const string OperationGroupList = "group/list";

    public const string PhaseQueueWait = "queue_wait";
    public const string PhaseHttpSend = "http_send";
    public const string PhaseHttpResponse = "http_response";
    public const string PhaseParse = "parse";

    public const string OutcomeOk = "ok";
    public const string OutcomeQueueTimeout = "queue_timeout";
    public const string OutcomeHttpTimeout = "http_timeout";
    public const string OutcomeCancelled = "cancelled";
    public const string OutcomeError = "error";
    public const string OutcomeHttpError = "http_error";
    public const string OutcomeUnknown = "unknown";
}
