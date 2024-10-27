using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OWSData.Models.Tables
{
    public partial class Profession
    {
        public int ProfessionId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
    }
}
