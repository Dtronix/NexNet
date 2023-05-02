using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NexNet;

public interface IIdentity
{
    string DisplayName { get; }
}

public class DefaultIdentity : IIdentity
{
    public string DisplayName { get; set; }
}
