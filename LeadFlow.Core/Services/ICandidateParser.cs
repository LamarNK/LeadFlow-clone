using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services;

public interface ICandidateParser
{
    CandidateName ParseName(string fullName);
    BitrixLeadPreview BuildPreview(CandidateResponse response, BitrixSettings settings);
}
