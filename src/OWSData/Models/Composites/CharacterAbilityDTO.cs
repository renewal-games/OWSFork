using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OWSData.Models.Composites
{
    public sealed class CharacterAbilityDto
    {
        public string AbilityIdTag { get; set; } = default!;
        public int CurrentAbilityLevel { get; set; }
        public int ActualAbilityLevel { get; set; }
        public string CustomData { get; set; } = string.Empty;
    }
}
