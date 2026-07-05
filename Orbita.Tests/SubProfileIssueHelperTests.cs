using Orbita.Api.Helpers;
using Orbita.Contracts;

namespace Orbita.Tests;

public sealed class SubProfileIssueHelperTests
{
    [Fact]
    public void StripMissingAttachmentIssues_ClearsIssue_WhenAttachmentMissing()
    {
        var attachmentId = Guid.Parse("f488a8ef-7c16-419e-9159-906928de618b");
        var profiles = new List<WorkerSubProfileDto>
        {
            new(
                "435179992",
                "контракт РФ 7",
                "Работа",
                false,
                null,
                "switch_failed",
                "не переключился",
                DateTime.UtcNow,
                true,
                attachmentId)
        };

        var sanitized = SubProfileIssueHelper.StripMissingAttachmentIssues(profiles, new HashSet<Guid>());

        Assert.NotNull(sanitized);
        Assert.Null(sanitized![0].LastIssueKind);
        Assert.Null(sanitized[0].LastIssueMessage);
        Assert.Null(sanitized[0].DiagnosticAttachmentId);
    }

    [Fact]
    public void TryClearIssueInJson_FindsSubProfile_ByName()
    {
        var json = """[{"id":"435179992","name":"контракт РФ 7","lastIssueKind":"switch_failed","lastIssueMessage":"не переключился","diagnosticAttachmentId":"f488a8ef-7c16-419e-9159-906928de618b"}]""";

        var cleared = SubProfileIssueHelper.TryClearIssueInJson(
            json,
            subProfileId: null,
            subProfileName: "контракт РФ 7",
            out var updatedJson);

        Assert.True(cleared);
        Assert.NotNull(updatedJson);

        var updated = SubProfileDeserializer.Deserialize(updatedJson);
        Assert.NotNull(updated);
        Assert.Single(updated!);
        Assert.Null(updated[0].LastIssueKind);
        Assert.Null(updated[0].LastIssueMessage);
        Assert.Null(updated[0].DiagnosticAttachmentId);
    }
}