using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Verse;

namespace StorytellerTelemetry
{
    /// <summary>
    /// Small non-blocking prompt shown once the aftermath of a crisis is over (colony stable again, a new crisis
    /// starts, it has dragged on for days, or nobody is left standing). Does not pause the game, is not saved,
    /// and can be ignored or closed. The answer is the human label for how hard the whole thing was.
    /// </summary>
    public class Dialog_RateFight : Window
    {
        private static readonly string[] Labels = { "Trivial", "Easy", "Good", "Hard", "Too much" };

        private readonly CrisisTrack crisis;
        private readonly string title;
        private readonly string summary;
        private readonly int ticksAfter;
        private readonly string status;
        private readonly Stopwatch shown = Stopwatch.StartNew();
        private bool endedRun;
        private bool answered;

        internal Dialog_RateFight(CrisisTrack crisis, string title, string summary, int ticksAfter, string status)
        {
            this.crisis = crisis;
            this.title = title;
            this.summary = summary;
            this.ticksAfter = ticksAfter;
            this.status = status;
            endedRun = status == "colony_incapacitated";
            layer = WindowLayer.GameUI;
            doCloseX = true;
            draggable = true;
            forcePause = false;
            preventCameraMotion = false;
            absorbInputAroundWindow = false;
            closeOnAccept = false;
            closeOnCancel = false;
            closeOnClickedOutside = false;
            focusWhenOpened = false;
            onlyOneOfTypeAllowed = false;
            drawShadow = false;
        }

        public override Vector2 InitialSize => new Vector2(580f, 190f);

        protected override void SetInitialSizeAndPosition()
        {
            Vector2 size = InitialSize;
            windowRect = new Rect(UI.screenWidth - size.x - 20f, 120f, size.x, size.y);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 24f, 70f),
                "How was it, aftermath included?\n" + title + "\n" + summary);
            Widgets.CheckboxLabeled(new Rect(inRect.x, inRect.yMax - 68f, 220f, 26f), "This ended my run", ref endedRun);
            float gap = 6f;
            float w = (inRect.width - gap * (Labels.Length - 1)) / Labels.Length;
            for (int i = 0; i < Labels.Length; i++)
            {
                Rect r = new Rect(inRect.x + i * (w + gap), inRect.yMax - 34f, w, 34f);
                if (Widgets.ButtonText(r, (i + 1) + "  " + Labels[i]))
                {
                    Answer(i + 1);
                }
            }
        }

        private JsonObj Record()
        {
            JsonObj o = Telemetry.Rec("rating")
                .I("crisis_id", crisis != null ? crisis.id : 0)
                .I("asked_ticks_after", ticksAfter)
                .S("asked_because", status)
                .B("ended_run", endedRun);
            if (crisis != null)
            {
                o.Strs("factors", crisis.factors).Ints("engagement_ids", crisis.engagementIds).Ints("incident_seqs", crisis.incidentSeqs);
            }
            return o;
        }

        internal void Answer(int score)
        {
            if (answered)
            {
                return;
            }
            answered = true;
            Telemetry.Write(Record()
                .I("score", score)
                .S("label", Labels[score - 1])
                .N("seconds_to_answer", shown.Elapsed.TotalSeconds));
            Close();
        }

        public override void PostClose()
        {
            base.PostClose();
            if (!answered)
            {
                answered = true;
                Telemetry.Write(Record().B("skipped", true));
            }
        }
    }
}
