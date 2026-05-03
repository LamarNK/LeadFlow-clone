using LeadFlow.Models;

namespace LeadFlow.Services;

public interface ICandidateParser
{
    CandidateName ParseName(string fullName);
    BitrixLeadPreview BuildPreview(CandidateResponse response, BitrixSettings settings);
}
