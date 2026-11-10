using System.Net.Mail;
using Merit.Application.Data;
using Merit.Application.Errors;
using Merit.Application.Security;
using Merit.Domain.Ids;
using Merit.Domain.Text;

namespace Merit.Application.Services;

public sealed class ContactCorrectionService(IContactCorrectionData data, ICurrentSession session)
{
    public Task<IReadOnlyList<ContactCorrectionRow>> ListAsync(Guid? personId, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        return data.ListAsync(personId, ct);
    }
    public async Task RequestAsync(ContactCorrectionInput input, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        input.ProposedValue = input.ProposedValue.Trim();
        input.SourceNote = input.SourceNote.Trim();
        input.City = input.City?.Trim();
        input.State = input.State?.Trim().ToUpperInvariant();
        input.Zip5 = input.Zip5?.Trim();
        input.Unit = string.IsNullOrWhiteSpace(input.Unit) ? null : input.Unit.Trim();
        input.ProposedAddressKey = null;
        var v = new Validator();
        v.Required("ProposedValue", input.ProposedValue, "Proposed contact").MaxLength("ProposedValue", input.ProposedValue, 200, "Proposed contact")
            .Required("SourceNote", input.SourceNote, "Source evidence").MaxLength("SourceNote", input.SourceNote, 1000, "Source evidence");
        v.When(input.ContactType is not ("phone" or "email" or "address"), "ContactType", "Choose phone, email or address.");
        v.When(input.UnitAction is not ("unspecified" or "set" or "remove"), "UnitAction", "Choose how to handle the apartment or unit.");
        v.MaxLength("Unit", input.Unit, 30, "Apartment / unit");
        v.When(input.ContactType != "address" && (input.UnitAction != "unspecified" || input.Unit is not null), "UnitAction", "Apartment/unit instructions apply only to address corrections.");
        v.When(input.UnitAction == "set" && MatchKeys.NormalizeUnit(input.Unit).Length == 0, "Unit", "Enter the apartment or unit to set.");
        v.When(input.UnitAction != "set" && input.Unit is not null, "Unit", "Choose set/replace to include a unit, or leave the unit field blank.");
        v.When(input.ContactType == "phone" && input.ProposedValue.Count(char.IsAsciiDigit) is < 10 or > 15, "ProposedValue", "Enter a telephone number with 10 to 15 digits.");
        v.When(input.ContactType == "email" && (!MailAddress.TryCreate(input.ProposedValue, out var email) || email.Address != input.ProposedValue), "ProposedValue", "Enter a valid email address without a display name.");
        if (input.ContactType == "address")
        {
            v.Required("City", input.City, "City").MaxLength("City", input.City, 80, "City");
            v.When(input.State?.Length != 2 || !input.State.All(char.IsAsciiLetter), "State", "Enter the two-letter state.");
            v.When(input.Zip5?.Length != 5 || !input.Zip5.All(char.IsAsciiDigit), "Zip5", "Enter a five-digit ZIP.");
            if (input.UnitAction is "set" or "remove")
            {
                input.ProposedAddressKey = MatchKeys.AddressKey(input.ProposedValue, input.Unit, input.City, input.State, input.Zip5);
                v.When(input.ProposedAddressKey.Length > 300, "ProposedValue", "The complete address exceeds the supported address length.");
            }
        }
        v.ThrowIfInvalid();
        input.ContactCorrectionId = TimeOrderedId.New();
        await data.RequestAsync(input, ct);
    }
    public Task ReviewAsync(Guid id, string status, string note, byte[]? version, CancellationToken ct)
    {
        session.Require(Permissions.PersonEdit);
        new Validator().Required("reviewNote", note, "Review evidence").MaxLength("reviewNote", note, 1000, "Review evidence")
            .When(status is not ("applied" or "rejected"), "requestStatus", "Choose applied or rejected.").ThrowIfInvalid();
        return data.ReviewAsync(id, status, note, version, ct);
    }
}
