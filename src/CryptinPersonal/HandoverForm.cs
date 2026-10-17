using CryptinSDK.Dongle;

namespace CryptinPersonal;

/// <summary>
/// The 24 words and the PIN, shown once, for a dongle that has just been
/// provisioned.
///
/// The acknowledgement is sent by the caller **after** this form closes with OK,
/// never from inside it: acknowledging is the irreversible step that stops the
/// dongle from ever handing the seed over again, so it must not happen on a form
/// the user might still be reading. That ordering is the whole mechanism — see
/// CRYPTIN_PROTOCOL.md §4.5.
///
/// The confirmation is a checkbox and not just a button because the button is
/// the one thing a user will click to make a dialog go away. Requiring the tick
/// first costs a second and is a defence against being dismissed unread — after
/// which nothing, on the chip or in this app, can bring the phrase back.
///
/// It is not the only defence any more. Since 2026-09-17 the tick hides the
/// words and asks for three of them back at positions picked at random, and the
/// button stays dead until they match — see the block next to the checkbox for
/// why, and for the three rules that change must not break. The tick alone was
/// never evidence of anything: it says the user believes they copied the phrase,
/// and the case that matters is the one where they believe it and are wrong.
/// </summary>
internal sealed class HandoverForm : Form
{
    public HandoverForm(ProvisioningHandover handover, string usbSerial)
    {
        Text = "Crypt-in — your recovery code";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 560);

        // No close button and no Escape: both are ways to lose the phrase by
        // reflex. The only way out is the tick and the button, or killing the
        // app -- which is survivable, because the dongle keeps offering the
        // handover until it is acknowledged.
        ControlBox = false;

        var heading = new Label
        {
            Text = "Write these 24 words and the PIN down on paper. Now.",
            Location = new Point(20, 18),
            Size = new Size(580, 26),
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
        };

        var detail = new Label
        {
            Text = $"Dongle {usbSerial}. They will not be shown again: not here, not by the dongle. " +
                   "They are not saved anywhere and they never go over the internet. " +
                   "If you lose the dongle, these words are the only way back to your files.",
            Location = new Point(20, 48),
            Size = new Size(580, 56),
        };

        var grid = new TableLayoutPanel
        {
            Location = new Point(20, 112),
            Size = new Size(580, 230),
            ColumnCount = 3,
            RowCount = 8,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = SystemColors.ControlLightLight,
        };
        for (var c = 0; c < 3; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (var r = 0; r < 8; r++)
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 8));

        // Numbered, and read down each column rather than across: that is how
        // the words get copied back in during a recovery, and a grid that reads
        // the other way is how a phrase gets written down out of order.
        var words = handover.Words;
        for (var i = 0; i < words.Count; i++)
        {
            var label = new Label
            {
                Text = $"{i + 1,2}. {words[i]}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(FontFamily.GenericMonospace, 10.5f),
            };
            grid.Controls.Add(label, i / 8, i % 8);
        }

        var pinLabel = new Label
        {
            Text = "PIN (6 digits):",
            Location = new Point(20, 356),
            Size = new Size(120, 24),
        };

        var pin = new Label
        {
            Text = handover.Pin,
            Location = new Point(140, 352),
            Size = new Size(160, 30),
            Font = new Font(FontFamily.GenericMonospace, 15f, FontStyle.Bold),
        };

        var pinNote = new Label
        {
            Text = "The PIN comes from the seed: you do not choose it and it cannot be changed. " +
                   "Three wrong PINs block the dongle, and unblocking it needs exactly these words.",
            Location = new Point(20, 388),
            Size = new Size(580, 40),
            ForeColor = SystemColors.GrayText,
        };

        var warning = new Label
        {
            Text = "Keep the paper away from the dongle. Whoever has both has everything.",
            Location = new Point(20, 432),
            Size = new Size(580, 22),
            ForeColor = Color.Firebrick,
        };

        var confirm = new CheckBox
        {
            Text = "I have written the 24 words and the PIN down on paper.",
            Location = new Point(20, 464),
            Size = new Size(420, 24),
        };

        var done = new Button
        {
            Text = "I'm done",
            Location = new Point(486, 462),
            Size = new Size(114, 30),
            Enabled = false,
            DialogResult = DialogResult.OK,
        };
        AcceptButton = done;

        var lastChance = new Label
        {
            Text = "Clicking “I'm done” stops the dongle showing them, permanently.",
            Location = new Point(20, 500),
            Size = new Size(580, 22),
            ForeColor = SystemColors.GrayText,
        };

        // --- The tick is not evidence. Ask for three of them back. ------------
        //
        // Decided 2026-09-17. Until then the tick enabled the button and nothing
        // else: no retyping, no check. The failure it leaves open is the one that
        // cannot be repaired later -- a phrase written down wrong is discovered on
        // the day the dongle is already lost, because the BIP-39 checksum only
        // speaks at recovery time.
        //
        // The rate is measured on us, not assumed: of five phrases transcribed by
        // hand into dongle-test-mnemonic.md, two were unusable -- one of 23 words,
        // one containing "genger", which is not a BIP-39 word. Written by people
        // who knew what they were holding, with the chip in front of them.
        //
        // Three rules this must never break:
        //  * it happens BEFORE the acknowledgement. 0x0A is still sent by the
        //    caller after OK, and is still the point of no return;
        //  * it cannot strand anyone. Unticking brings the words straight back,
        //    there is no attempt counter, and nothing here touches the dongle --
        //    getting it wrong costs a retry and nothing else;
        //  * the words are HIDDEN while it is asked, or it tests nothing but the
        //    user's ability to copy from a screen.
        var quizPositions = Enumerable.Range(0, words.Count)
            .OrderBy(_ => Random.Shared.Next())
            .Take(QuizWords)
            .OrderBy(position => position)
            .ToArray();

        var quiz = new Panel
        {
            Location = grid.Location,
            Size = grid.Size,
            Visible = false,
        };

        var quizHeading = new Label
        {
            Text = "Now write three of them back — from the paper, not from memory.",
            Location = new Point(0, 4),
            Size = new Size(580, 24),
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
        };

        var quizDetail = new Label
        {
            Text = "The words are hidden while you do this, on purpose. If the paper does not " +
                   "match, untick the box below and they come back — nothing is lost and there " +
                   "is no limit on tries.",
            Location = new Point(0, 30),
            Size = new Size(570, 40),
            ForeColor = SystemColors.GrayText,
        };

        var answers = new TextBox[quizPositions.Length];
        var marks = new Label[quizPositions.Length];
        for (var i = 0; i < quizPositions.Length; i++)
        {
            quiz.Controls.Add(new Label
            {
                Text = $"Word {quizPositions[i] + 1}:",
                Location = new Point(0, 82 + i * 36),
                Size = new Size(70, 24),
                TextAlign = ContentAlignment.MiddleLeft,
            });
            answers[i] = new TextBox
            {
                Location = new Point(76, 78 + i * 36),
                Size = new Size(200, 26),
                Font = new Font(FontFamily.GenericMonospace, 10.5f),
            };
            marks[i] = new Label
            {
                Location = new Point(288, 82 + i * 36),
                Size = new Size(260, 24),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            quiz.Controls.Add(answers[i]);
            quiz.Controls.Add(marks[i]);
        }
        quiz.Controls.AddRange([quizHeading, quizDetail]);

        // Case and stray spaces are not what this is testing: someone who wrote
        // "Abandon " on paper wrote the right word down.
        bool Matches(int i) => string.Equals(
            answers[i].Text.Trim(), words[quizPositions[i]], StringComparison.OrdinalIgnoreCase);

        void Evaluate()
        {
            var all = true;
            for (var i = 0; i < answers.Length; i++)
            {
                var typed = answers[i].Text.Trim().Length > 0;
                var ok = typed && Matches(i);
                // Silent until something is typed: marking an untouched box wrong
                // reads as an accusation before the user has done anything.
                marks[i].Text = !typed ? "" : ok ? "✓" : "does not match the paper";
                marks[i].ForeColor = ok ? Color.ForestGreen : Color.Firebrick;
                all &= ok;
            }
            done.Enabled = all;
        }

        foreach (var box in answers) box.TextChanged += (_, _) => Evaluate();

        confirm.CheckedChanged += (_, _) =>
        {
            // Hiding the PIN too was considered and dropped: it derives from the
            // seed, so these words restore it, and leaving it on screen lets
            // someone who ticked early still copy it down. The words are the half
            // that nothing else can rebuild.
            grid.Visible = !confirm.Checked;
            quiz.Visible = confirm.Checked;

            if (!confirm.Checked)
            {
                foreach (var box in answers) box.Clear();
                done.Enabled = false;
                return;
            }

            Evaluate();
            answers[0].Focus();
        };

        Controls.AddRange([heading, detail, grid, quiz, pinLabel, pin, pinNote, warning,
                           confirm, done, lastChance]);
    }

    /// <summary>
    /// How many of the 24 are asked back. Three is what hardware wallets settle
    /// on: enough that a phrase copied carelessly fails, few enough that a user
    /// who did the work is not punished for it.
    /// </summary>
    private const int QuizWords = 3;
}
