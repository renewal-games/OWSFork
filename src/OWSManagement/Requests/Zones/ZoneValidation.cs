using OWSManagement.DTOs;

namespace OWSManagement.Requests.Zones
{
    /// <summary>
    /// Shared checks for the add and edit paths. These mirror the Maps column widths, so a
    /// too-long name comes back as a readable message instead of a truncation or a driver error.
    /// </summary>
    internal static class ZoneValidation
    {
        internal const int MapNameMaxLength = 50;
        internal const int ZoneNameMaxLength = 50;
        internal const int ContainsFilterMaxLength = 100;
        internal const int ListFilterMaxLength = 200;

        /// <summary>
        /// Returns null when the DTO is usable, otherwise the reason it is not. Trims the
        /// string fields in place so a trailing space never becomes part of a zone name the
        /// launcher then fails to match.
        /// </summary>
        internal static string Validate(AddOrUpdateZoneDTO dto)
        {
            if (dto == null)
            {
                return "No zone was supplied.";
            }

            dto.MapName = (dto.MapName ?? string.Empty).Trim();
            dto.ZoneName = (dto.ZoneName ?? string.Empty).Trim();
            dto.WorldCompContainsFilter = (dto.WorldCompContainsFilter ?? string.Empty).Trim();
            dto.WorldCompListFilter = (dto.WorldCompListFilter ?? string.Empty).Trim();

            if (dto.MapName.Length == 0)
            {
                return "Map Name is required.";
            }

            if (dto.ZoneName.Length == 0)
            {
                return "Zone Name is required.";
            }

            if (dto.MapName.Length > MapNameMaxLength)
            {
                return $"Map Name cannot be longer than {MapNameMaxLength} characters.";
            }

            if (dto.ZoneName.Length > ZoneNameMaxLength)
            {
                return $"Zone Name cannot be longer than {ZoneNameMaxLength} characters.";
            }

            if (dto.WorldCompContainsFilter.Length > ContainsFilterMaxLength)
            {
                return $"World Comp Contains Filter cannot be longer than {ContainsFilterMaxLength} characters.";
            }

            if (dto.WorldCompListFilter.Length > ListFilterMaxLength)
            {
                return $"World Comp List Filter cannot be longer than {ListFilterMaxLength} characters.";
            }

            if (dto.SoftPlayerCap < 0 || dto.HardPlayerCap < 0)
            {
                return "Player caps cannot be negative.";
            }

            if (dto.HardPlayerCap > 0 && dto.SoftPlayerCap > dto.HardPlayerCap)
            {
                return "Soft Player Cap cannot exceed Hard Player Cap.";
            }

            if (dto.MinutesToShutdownAfterEmpty < 0)
            {
                return "Minutes To Shutdown After Empty cannot be negative.";
            }

            return null;
        }
    }
}
