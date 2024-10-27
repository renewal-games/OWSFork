using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OWSData.Models.Tables
{
    public abstract partial class CharacterProfessionBase
    {
        public int CharacterId { get; set; }
        public int ProfessionId { get; set; }
        public int Level { get; set; }
        public long Exp { get; set; }

        public virtual Characters Character { get; set; }
        public virtual Profession Profession { get; set; }
    }
}
