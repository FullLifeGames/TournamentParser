using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TournamentParser.Data;

namespace TournamentParser.Finalizer
{
    public interface IFinalizer
    {
        void Finalize(IDictionary<string, User> nameUserTranslation);
    }
}
