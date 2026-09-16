using System.Security.Cryptography;

using TripMate.Application.Common.Interfaces;
using TripMate.Domain.Constants;

namespace TripMate.Application.Features.TravelGroups.ManageInvitation;

public sealed class RandomGroupInvitationCodeGenerator : IGroupInvitationCodeGenerator
{
    public string Generate()
    {
        var characters = new char[TravelGroupConstants.InvitationCodeLength];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = TravelGroupConstants.InvitationCodeCharacters[
                RandomNumberGenerator.GetInt32(TravelGroupConstants.InvitationCodeCharacters.Length)];
        }

        return new string(characters);
    }
}