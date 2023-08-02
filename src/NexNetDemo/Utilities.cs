using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NexNetDemo;

internal static class Utilities
{

    /// <summary>
    /// Rolling average function that takes a new sample and returns the average of the last 100 samples.
    /// </summary>
    /// <param name="avg"></param>
    /// <param name="newSample"></param>
    /// <returns></returns>
    public static double ApproxRollingAverage(double avg, double newSample)
    {
        avg -= avg / 100;
        avg += newSample / 100;
        return avg;
    }
}
