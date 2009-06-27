#!/usr/bin/csi -s

Console.WriteLine ("Total arguments: {0}", CommandLineArgs.Count);

foreach (string arg in CommandLineArgs) {
    Console.WriteLine (arg);
}
