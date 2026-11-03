using System;
using System.Collections.Generic;
using System.Text;

namespace OWSData.Models.StoredProcs
{
    public class GetServerInstanceFromPort
    {
		public string MapName { get; set; }
		public string ZoneName { get; set; }
		public string WorldCompContainsFilter { get; set; }
		public string WorldCompListFilter { get; set; }
		public int MapInstanceID { get; set; }
		public int WorldServerID { get; set; }
		public int Port { get; set; }
		public int Status { get; set; }
		public int MaxNumberOfInstances { get; set; }
		public DateTime? ActiveStartTime { get; set; }
		public byte ServerStatus { get; set; }
		public string InternalServerIP { get; set; }

		//Whether a spin-up command describes this row. Read from GetZoneInstanceForSpinUp, the only lookup that returns all of these.
		public bool MatchesSpinUp(int worldServerID, int zoneInstanceID, string mapName, int port)
		{
			return MapInstanceID > 0
				&& MapInstanceID == zoneInstanceID
				&& WorldServerID == worldServerID
				&& Port == port
				&& string.Equals(MapName, mapName, StringComparison.Ordinal);
		}
	}
}
