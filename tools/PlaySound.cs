using System;
using System.Media;
using System.Threading;

// Minimal helper: play a looping sound for N seconds, then stop.
// Used to check that SleepGuard postpones the display-off while audio plays.
internal static class PlaySound
{
    private static int Main(string[] args)
    {
        int seconds = 30;
        if (args.Length > 0) int.TryParse(args[0], out seconds);
        string file = args.Length > 1 ? args[1] : @"C:\Windows\Media\Alarm01.wav";
        try
        {
            using (SoundPlayer player = new SoundPlayer(file))
            {
                player.PlayLooping();
                Thread.Sleep(seconds * 1000);
                player.Stop();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("play failed: " + ex.Message);
            return 1;
        }
    }
}
