using System;
using System.Diagnostics;
using System.Threading;
using HoverLex;

class BenchmarkProofreader
{
    static void Main()
    {
        EnglishProofreader engine = new EnglishProofreader();
        foreach (string text in new[] { "She go to school every day.", "I has an apple.", "Hello, world.", "recieve", "recieve" })
        {
            Stopwatch watch = Stopwatch.StartNew();
            string result = engine.CorrectAsync(text, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(watch.ElapsedMilliseconds + " ms | " + text + " -> " + result);
        }
    }
}
