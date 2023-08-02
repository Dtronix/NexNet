using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NexNet.Messages;
using NexNet.Transports;
using NexNetDemo.Samples.DuplexPipe;
using NexNetDemo.Samples.DuplexPipeStreaming;
using Pipelines.Sockets.Unofficial.Buffers;

namespace NexNetDemo;

internal class Program
{
    static async Task Main(string[] args)
    {
        await new DuplexPipeSimpleSample().UploadSample();
        //await new DuplexPipeStreamingSample().DuplexStreamingSample();


        Console.ReadLine();
    }
    
}
