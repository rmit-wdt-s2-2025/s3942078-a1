// <TargetFramework>net9.0</TargetFramework>
using SimpleHashing.Net;
using System;

class Program
{
    static void Main(string[] args)
    {
        Console.Write("Enter password: ");
        var pwd = ReadHidden();
        var hasher = new SimpleHash();
        var hash = hasher.Compute(pwd);
        Console.WriteLine("\nHASH:");
        Console.WriteLine(hash);
    }

    static string ReadHidden()
    {
        var buf = new System.Collections.Generic.List<char>();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace && buf.Count > 0) { buf.RemoveAt(buf.Count - 1); Console.Write("\b \b"); }
            else if (!char.IsControl(k.KeyChar)) { buf.Add(k.KeyChar); Console.Write('*'); }
        }
        return new string(buf.ToArray());
    }
}
