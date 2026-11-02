using OWSData.Models.Composites;
using OWSData.Repositories.Interfaces;
using OWSShared.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace OWSCharacterPersistence.Requests.Characters
{
    public class UpdateAllPlayerPositionsRequest
    {
        public string SerializedPlayerLocationData { get; set; }
        public string MapName { get; set; }

        private Guid customerGUID;
        private ICharactersRepository charactersRepository;

        public void SetData(ICharactersRepository charactersRepository, IHeaderCustomerGUID customerGuid)
        {
            this.charactersRepository = charactersRepository;
            customerGUID = customerGuid.CustomerGUID;
        }

        public async Task<SuccessAndErrorMessage> Handle()
        {
            SuccessAndErrorMessage successAndErrorMessage = new SuccessAndErrorMessage();

            // One batch carries every player on the map. Unguarded indexing and culture-sensitive
            // parsing meant a single short segment (IndexOutOfRange) or a comma-decimal locale on
            // the zone server host (FormatException) threw part-way through the loop, so every
            // player after the bad entry silently lost their position save and the caller got a 500
            // instead of a result. Each entry now stands or falls on its own.
            List<string> skipped = new List<string>();

            foreach (string PlayerDataString in (SerializedPlayerLocationData ?? String.Empty).Split('|'))
            {
                if (String.IsNullOrWhiteSpace(PlayerDataString))
                {
                    continue;
                }

                string[] PlayerDataValues = PlayerDataString.Split(':');

                if (PlayerDataValues.Length < 7 || String.IsNullOrWhiteSpace(PlayerDataValues[0]))
                {
                    skipped.Add(PlayerDataString);
                    continue;
                }

                string PlayerName = PlayerDataValues[0];

                if (!TryParseLocation(PlayerDataValues[1], out float X)
                    || !TryParseLocation(PlayerDataValues[2], out float Y)
                    || !TryParseLocation(PlayerDataValues[3], out float Z)
                    || !TryParseRotation(PlayerDataValues[4], out float RX)
                    || !TryParseRotation(PlayerDataValues[5], out float RY)
                    || !TryParseRotation(PlayerDataValues[6], out float RZ))
                {
                    skipped.Add(PlayerName);
                    continue;
                }

                // The 8th field is the owner token; a character with an owner takes its position only from that owner.
                Guid? ownerToken = PlayerDataValues.Length > 7 && Guid.TryParse(PlayerDataValues[7], out Guid parsedToken)
                    ? parsedToken
                    : null;

                try
                {
                    // The ownership check is part of the write, so a handover cannot land between the two.
                    if (!await charactersRepository.UpdatePosition(customerGUID, PlayerName, MapName, X, Y, Z, RX, RY, RZ, ownerToken))
                    {
                        skipped.Add(PlayerName);
                    }
                }
                catch
                {
                    // One character's write failing must not cost the rest of the map its save.
                    // The exception message is deliberately not echoed back — it carries DB and SQL
                    // detail to a caller that cannot act on it.
                    skipped.Add(PlayerName);
                }
            }

            // Partial success is still success. Reporting false here would make a zone server that
            // retries on !Success resend the whole map every tick without ever converging, because a
            // malformed entry is malformed on every retry too. The skipped names go in ErrorMessage
            // as a diagnostic, not as a signal to retry.
            successAndErrorMessage.Success = true;
            successAndErrorMessage.ErrorMessage = skipped.Count == 0
                ? String.Empty
                : $"Skipped {skipped.Count} malformed or failed position entries: {String.Join(", ", skipped)}";

            return successAndErrorMessage;
        }

        // UE5's world bound (UE_LARGE_HALF_WORLD_MAX, in cm). Nothing in a level lies outside it.
        public const double MaxWorldCoordinate = 4398046511104.0;

        // The zone server always serializes with '.' as the decimal separator; parsing with the
        // server's current culture is what makes this locale-dependent.
        // NumberStyles.Float also accepts "NaN" and "Infinity". Saved, either becomes the character's
        // spawn point, and the zone server cannot place a character there.
        private static bool TryParseRotation(string value, out float parsed) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
            && float.IsFinite(parsed);

        // Finite but outside the world is as unusable a spawn point.
        private static bool TryParseLocation(string value, out float parsed) =>
            TryParseRotation(value, out parsed) && Math.Abs(parsed) <= MaxWorldCoordinate;
    }
}
