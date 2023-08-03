using NexNetDemo.Samples.InvocationSample;

namespace NexNetDemo;

internal class Program
{
    static async Task Main(string[] args)
    {
        //await new DuplexPipeSimpleSample().UploadSample();
        //await new DuplexPipeStreamingSample().DuplexStreamingSample();
        await new InvocationSample().UpdateInfo();


        Console.ReadLine();
    }
    
}
