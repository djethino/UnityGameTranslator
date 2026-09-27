using System;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A line the translation server answered with an error: blamed only when the server was seen
    /// working on something else between two such errors. Replays the case that sent a repetitive
    /// placeholder to the model at every appearance, and the outage that must file nothing.
    /// </summary>
    internal static class ServerErrorBlameChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // The case seen in a game: the line fails, another line is translated, the line fails again.
            var blame = new ServerErrorBlame();
            check(!blame.Blames("对话对话对话"), "a first error blames nobody",
                  "one error cannot tell a text the model chokes on from a server that just fell over");
            blame.Answered();
            check(blame.Blames("对话对话对话"), "the same error after the server translated something else blames the line",
                  "the server works, and this text is what it cannot do — asked again, it would cost the same seconds for ever");
            check(!blame.Blames("对话对话对话"), "once blamed, the line starts over here",
                  "the failure record takes over; a retranslation from the tab is judged afresh");

            // An outage: every line fails, nothing is translated in between.
            var outage = new ServerErrorBlame();
            outage.Answered();                        // it worked before it broke
            outage.Blames("Play");
            outage.Blames("Quit");
            check(!outage.Blames("Play") && !outage.Blames("Quit"),
                  "a server answering every line with an error blames none of them",
                  "filing an outage would bury the failures tab under lines with nothing wrong");

            // Recovery: the lines that failed during the outage fail again only if the server,
            // working again, still cannot do them.
            outage.Answered();
            check(outage.Blames("Play"), "an error after the server came back blames the line it still fails on",
                  "the server translated something since: the error is this line's");

            check(!new ServerErrorBlame().Blames(""), "an empty key blames nothing", "there is no line to file");
        }
    }
}
