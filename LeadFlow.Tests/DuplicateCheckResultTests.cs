using LeadFlow.Core.Models;
using Xunit;

namespace LeadFlow.Tests;

public sealed class DuplicateCheckResultTests
{
    [Fact]
    public void LocalDuplicate_IsDuplicate_AndSummaryReportsDuplicate()
    {
        var sut = new DuplicateCheckResult { IsLocalDuplicate = true };
        Assert.True(sut.IsDuplicate);
        Assert.False(sut.ShouldDeferBitrixSend);
        Assert.Equal("Дубль найден — сделка не создаётся", sut.Summary);
    }

    [Fact]
    public void BitrixDuplicate_IsDuplicate_AndSummaryReportsDuplicate()
    {
        var sut = new DuplicateCheckResult { IsBitrixDuplicate = true };
        Assert.True(sut.IsDuplicate);
        Assert.False(sut.ShouldDeferBitrixSend);
        Assert.Equal("Дубль найден — сделка не создаётся", sut.Summary);
    }

    [Fact]
    public void BitrixUnavailable_DefersBitrixSend_AndSummaryStartsWithUnavailableHeader()
    {
        var sut = new DuplicateCheckResult
        {
            IsBitrixCheckUnavailable = true,
            BitrixCheckUnavailableReason = "HTTP 500"
        };

        Assert.True(sut.ShouldDeferBitrixSend);
        Assert.False(sut.IsDuplicate);
        Assert.StartsWith("Проверка дублей в Bitrix24 недоступна", sut.Summary);
        Assert.Contains("HTTP 500", sut.Summary);
    }

    [Fact]
    public void NoDuplicates_NotDuplicate_NoDefer_HappyPathSummary()
    {
        var sut = new DuplicateCheckResult();
        Assert.False(sut.IsDuplicate);
        Assert.False(sut.ShouldDeferBitrixSend);
        Assert.Equal("Дубль не найден — сделка будет создана автоматически", sut.Summary);
    }

    [Fact]
    public void BitrixUnavailable_TakesPriorityOverDuplicateInSummary()
    {
        var sut = new DuplicateCheckResult
        {
            IsLocalDuplicate = true,
            IsBitrixCheckUnavailable = true,
            BitrixCheckUnavailableReason = "timeout"
        };

        Assert.True(sut.IsDuplicate);
        Assert.True(sut.ShouldDeferBitrixSend);
        Assert.StartsWith("Проверка дублей в Bitrix24 недоступна", sut.Summary);
    }
}
