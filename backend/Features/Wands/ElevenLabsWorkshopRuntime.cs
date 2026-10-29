using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.PersonaLab;

namespace Kufr.Backend.Features.Wands;

internal sealed class ElevenLabsWorkshopRuntime(WandGenerationStore wands, IWorkshopSpeechProvider speech,
    IMobileWandQuestionProvider dialogue, ILogger<ElevenLabsWorkshopRuntime> logger)
{
    internal async Task RunAsync(MobileWandAdmission admission, WebSocket socket,
        Func<CancellationToken, Task> requireAuthority, CancellationToken requestToken,
        Func<LabSocketSender, IWorkshopSpeechProvider>? speechFactory = null)
    {
        using var life = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        // The cross-process owner lease lasts 210 seconds; audio ends before it.
        life.CancelAfter(TimeSpan.FromSeconds(180));
        var token = life.Token;
        var sender = new LabSocketSender(socket);
        var decisions = Channel.CreateBounded<byte>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        // No I/O loops start until authority and the immutable voice capture succeed.
        WorkshopPlayback playback = null!;
        var commits = new HashSet<string>(StringComparer.Ordinal);
        var inputState = new object();
        var transcriptGate = new SemaphoreSlim(1, 1);
        long transcriptSequence = 0;
        long newestTranscriptSequence = 0;
        long inputRevision = 0;
        var inputPending = false;
        string? inputEventId = null;
        IWorkshopSpeechProvider connectionSpeech = null!;
        CancellationTokenSource? activeInference = null;
        var decisionCount = 0;
        string? assessedEvidence = null;
        Task[] running = [];
        Task<bool>? verdictPlayback = null;
        Task? heldSpeech = null;
        try
        {
            if (!admission.ElevenLabsConsent) { throw new WandGenerationException("elevenlabs_consent_required", 403); }
            await requireAuthority(token);
            connectionSpeech = speechFactory?.Invoke(sender) ?? speech.Capture();
            playback = new WorkshopPlayback(connectionSpeech, sender, token);
            if (wands.ReadWorkshopState(admission.Owner, admission.Operation).Status != "asking")
            {
                throw new WandGenerationException("wand_conversation_closed", 409);
            }
            await using var recognition = await connectionSpeech.ConnectAsync(token);
            await sender.JsonAsync(new { type = "ready", model = "elevenlabs-scribe-gpt", playbackMode = "stream" }, token);
            if (connectionSpeech is not SpeechEngineSpeechProvider) decisions.Writer.TryWrite(0);
            running = [InputAsync(recognition), RecognizeAsync(recognition), DecideAsync(), GuardAsync()];
            await await Task.WhenAny(running);
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var code = error switch
            {
                WandGenerationException wand => wand.Code,
                MobileWandQuestionAuthoringException authoring => authoring.Code,
                _ => "elevenlabs_transcription_failed"
            };
            logger.LogWarning("Workshop voice connection ended: {FailureCode}", code);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await sender.JsonAsync(new { type = "error", code }, deadline.Token); } catch (Exception) { }
        }
        finally
        {
            life.Cancel();
            playback?.Interrupt();
            decisions.Writer.TryComplete();
            // An accepted decision may finish durably after audio has stopped. Its
            // separate deadline still checks authority and the exact guest evidence.
            try { await Task.WhenAll(running); } catch (Exception) { }
            if (verdictPlayback is not null)
            {
                try { await verdictPlayback; } catch (Exception) { }
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await sender.JsonAsync(new { type = "ended" }, deadline.Token);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ended", deadline.Token);
                }
                catch (Exception) { socket.Abort(); }
            }
        }

        async Task InputAsync(IWorkshopRecognitionConnection recognition)
        {
            long total = 0;
            while (!token.IsCancellationRequested)
            {
                var message = await MobileWorkshopLiveRuntime.ReceiveClientMessageAsync(socket, token);
                if (message is null) { return; }
                if (message.Value.Type == WebSocketMessageType.Binary)
                {
                    total += message.Value.Payload.Length;
                    if (message.Value.Payload.Length % 2 != 0 || total > 24000L * 2 * 180)
                    {
                        throw new InvalidDataException("Audio exceeded its bound.");
                    }
                    await recognition.SendAudioAsync(message.Value.Payload, token);
                }
                else
                {
                    using var command = JsonDocument.Parse(message.Value.Payload);
                    var type = command.RootElement.GetProperty("type").GetString();
                    if (type == "end") { return; }
                    if (type == "cancel")
                    {
                        playback.Interrupt();
                        await sender.JsonAsync(new { type = "interrupted" }, token);
                    }
                    if (type == "speech_engine_activity" && recognition is SpeechEngineRecognition engineRecognition)
                    {
                        if (!command.RootElement.TryGetProperty("eventId", out var activity)
                            || !activity.TryGetInt64(out var eventId) || eventId < 1 || eventId > 9007199254740991L)
                            throw new InvalidDataException("Invalid Speech Engine activity.");
                        engineRecognition.Activity(eventId);
                    }
                }
            }
        }

        async Task RecognizeAsync(IWorkshopRecognitionConnection recognition)
        {
            TranscriptTurn? turn = null;
            while (!token.IsCancellationRequested)
            {
                var item = await recognition.ReceiveAsync(token);
                if (item is null) { return; }
                if (item.SpeechStarted)
                {
                    if (recognition is SpeechEngineRecognition engine && !engine.IsPendingActivity(item.EventId)) continue;
                    CancellationTokenSource? interruptedInference;
                    lock (inputState) { inputRevision++; inputPending = true; interruptedInference = activeInference; }
                    try { interruptedInference?.Cancel(); } catch (ObjectDisposedException) { }
                    playback.Interrupt();
                    continue;
                }
                if (string.IsNullOrWhiteSpace(item.Text)) { continue; }
                // Some provider variants supply IDs; retransmission must not add
                // evidence twice. Equal text with no ID can be a real repeated turn.
                if (item.Committed && item.EventId is not null && !commits.Add(item.EventId)) { continue; }
                if (commits.Count > 64) { throw new WandGenerationException("wand_dialogue_limit", 413); }
                if (!item.Committed && turn?.Text == item.Text) { continue; }
                turn ??= new("user");
                CancellationTokenSource? superseded;
                lock (inputState)
                {
                    inputRevision++;
                    inputPending = true;
                    superseded = activeInference;
                }
                // Stop only cancels playback. Actual new speech supersedes the
                // old inference too, so its next commit is not held for 45 seconds.
                try { superseded?.Cancel(); } catch (ObjectDisposedException) { }
                playback.Interrupt();
                await sender.JsonAsync(new { type = "interrupted" }, token);
                if (!item.Committed)
                {
                    await SnapshotAsync(turn, item.Text, false, token);
                    continue;
                }
                await requireAuthority(token);
                bool accepted;
                lock (inputState)
                {
                    accepted = wands.ReadWorkshopState(admission.Owner, admission.Operation).Status == "asking";
                    if (accepted)
                    {
                        wands.AppendWorkshopMessage(admission.Owner, admission.Operation, Guid.NewGuid().ToString("N"),
                            "user", item.Text, "voice");
                    }
                }
                if (recognition is SpeechEngineRecognition)
                    await sender.JsonAsync(new { type = "speech_engine_user_committed", eventId = SpeechEngineProtocol.EventId(item.EventId) }, token);
                await SnapshotAsync(turn, item.Text, true, token);
                turn = null;
                lock (inputState)
                {
                    inputPending = item.MoreSpeechPending || recognition is SpeechEngineRecognition engine && engine.HasPendingAfter(item.EventId);
                    inputEventId = item.EventId;
                    inputRevision++;
                }
                if (accepted && !decisions.Writer.TryWrite(0)) { throw new WandGenerationException("wand_dialogue_limit", 413); }
            }
        }

        async Task DecideAsync()
        {
            await foreach (var _ in decisions.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                if (wands.ReadWorkshopState(admission.Owner, admission.Operation).Status != "asking") { continue; }
                MobileWandWorkshopMessage[] messages;
                long observedInput;
                string? observedEventId;
                lock (inputState)
                {
                    if (inputPending) { continue; }
                    messages = wands.ReadWorkshopMessages(admission.Owner, admission.Operation);
                    observedInput = inputRevision;
                    observedEventId = inputEventId;
                }
                var replySpeech = connectionSpeech.ForReply(observedEventId);
                var evidence = WandGenerationStore.UserEvidenceDigest(messages);
                if (evidence == assessedEvidence) { continue; }
                assessedEvidence = evidence;
                if (++decisionCount > 32) { throw new WandGenerationException("wand_dialogue_limit", 413); }
                var version = playback.Version;
                using var decisionLife = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                using var audioLife = CancellationTokenSource.CreateLinkedTokenSource(token, decisionLife.Token);
                lock (inputState)
                {
                    if (inputPending || observedInput != inputRevision) { continue; }
                    activeInference = decisionLife;
                }
                var clauses = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
                { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                var spokenPrefix = new System.Text.StringBuilder();
                var transcript = new TranscriptTurn("assistant");
                var completed = false;
                var holdSpeechCompletion = false;
                var speaking = SpeakClausesAsync();
                try
                {
                    await requireAuthority(decisionLife.Token);
                    var draft = await dialogue.ReplyStreamingAsync(admission.Profile, messages, async (text, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (text.Length > 600 || spokenPrefix.Length + text.Length > 600)
                        {
                            throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
                        }
                        spokenPrefix.Append(text);
                        if (token.IsCancellationRequested) { return; }
                        try { await clauses.Writer.WriteAsync(text, audioLife.Token); }
                        catch (OperationCanceledException) when (!decisionLife.IsCancellationRequested) { }
                    }, async (text, cancellation) =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (token.IsCancellationRequested) { return; }
                        try { await SnapshotAsync(transcript, text, false, audioLife.Token, CurrentInput); }
                        catch (OperationCanceledException) when (!decisionLife.IsCancellationRequested) { }
                    }, decisionLife.Token, admission.DialogueInstructions, admission.ReasoningEffort);
                    var prefix = spokenPrefix.ToString().TrimStart();
                    if (!WandGenerationValidation.Text(draft.Reply, 600)
                        || !draft.Reply.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        throw new MobileWandQuestionAuthoringException("dialogue_response_invalid", false);
                    }
                    var captured = false;
                    try { await requireAuthority(decisionLife.Token); }
                    catch (WandGenerationException error) when (error.Code == "wand_workshop_boss_captured")
                    {
                        // Account/profile checks precede boss capture. Preserve
                        // the completed decision for rescue without starting work.
                        captured = true;
                    }
                    lock (inputState)
                    {
                        using var replyLease = replySpeech.EnterReply();
                        if (replyLease is null) continue;
                        if (inputPending || observedInput != inputRevision) { continue; }
                        var current = wands.ReadWorkshopMessages(admission.Owner, admission.Operation);
                        var status = wands.ReadWorkshopState(admission.Owner, admission.Operation).Status;
                        if (!MobileWorkshopLiveRuntime.SameUserEvidence(messages, current)
                            || status != "asking" && !(captured && status == "paused")) { continue; }
                        wands.AppendWorkshopMessage(admission.Owner, admission.Operation, Guid.NewGuid().ToString("N"),
                            "assistant", draft.Reply, "voice");
                        if (draft.ReadyToFinish && messages.Any(message => message.Role == "user"))
                        {
                            try
                            {
                                if (connectionSpeech is SpeechEngineSpeechProvider) Volatile.Write(ref heldSpeech, speaking);
                                wands.FinishWorkshopConversation(admission.Owner, admission.Operation,
                                    admission.Profile.ProfileId, admission.Profile.Revision, evidence);
                                Volatile.Write(ref holdSpeechCompletion, connectionSpeech is SpeechEngineSpeechProvider);
                            }
                            catch (WandGenerationException error) when (error.Code == "wand_conversation_changed") { continue; }
                        }
                    }
                    if (captured) { throw new WandGenerationException("wand_workshop_boss_captured", 409); }
                    if (!token.IsCancellationRequested)
                    {
                        await SnapshotAsync(transcript, draft.Reply, true, audioLife.Token, CurrentInput);
                        await sender.JsonAsync(new { type = "state", session = wands.ReadWorkshopState(admission.Owner, admission.Operation) }, token);
                        var tail = draft.Reply[prefix.Length..];
                        if (tail.Length > 0) { await clauses.Writer.WriteAsync(tail, audioLife.Token); }
                    }
                    completed = true;
                }
                catch (OperationCanceledException) when (decisionLife.IsCancellationRequested)
                {
                    if (speaking.IsFaulted) { await speaking; }
                    lock (inputState)
                    {
                        if (observedInput == inputRevision && !token.IsCancellationRequested) { throw; }
                    }
                }
                finally
                {
                    if (!completed) { playback.Interrupt(); }
                    clauses.Writer.TryComplete();
                    lock (inputState)
                    {
                        if (ReferenceEquals(activeInference, decisionLife)) { activeInference = null; }
                    }
                    await speaking;
                }

                async Task SpeakClausesAsync()
                {
                    var previous = new System.Text.StringBuilder();
                    try
                    {
                        await foreach (var text in clauses.Reader.ReadAllAsync(audioLife.Token))
                        {
                            await playback.SpeakAsync(text, version, previous.ToString(), replySpeech);
                            previous.Append(text);
                        }
                        // Speech Engine responses must belong to a user turn.
                        // Keep the manufacture turn open until its saved verdict.
                        if (!Volatile.Read(ref holdSpeechCompletion)) await replySpeech.CompleteReplyAsync(audioLife.Token);
                    }
                    catch (OperationCanceledException) when (audioLife.IsCancellationRequested) { }
                    catch { decisionLife.Cancel(); throw; }
                }

                bool CurrentInput()
                {
                    lock (inputState) { return !inputPending && observedInput == inputRevision && replySpeech.IsCurrentReply; }
                }
            }
        }

        async Task GuardAsync()
        {
            string? lastStatus = null;
            var verdictSent = false;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token);
                await requireAuthority(token);
                if (verdictPlayback is { IsFaulted: true }) { await verdictPlayback; }
                if (verdictPlayback is { IsCompletedSuccessfully: true } && !verdictPlayback.Result)
                { verdictSent = false; verdictPlayback = null; }
                var state = wands.ReadWorkshopState(admission.Owner, admission.Operation);
                if (state.Status != lastStatus)
                {
                    lastStatus = state.Status;
                    await sender.JsonAsync(new { type = "state", session = state }, token);
                    if (state.Status == "failed")
                    {
                        await sender.JsonAsync(new { type = "error", code = state.ErrorCode ?? "wand_generation_failed" }, token);
                    }
                }
                if (state.Status == "complete" && state.Wand is not null && !verdictSent)
                {
                    var version = playback.Version;
                    var verdict = state.VerdictText();
                    string? eventId;
                    lock (inputState) { eventId = inputEventId; }
                    var verdictSpeech = connectionSpeech.ForReply(eventId);
                    verdictSent = true;
                    // Publication waits for the closing reply too; otherwise a
                    // fast manufacture could publish the verdict before its tail.
                    verdictPlayback = SpeakVerdictAsync(verdict, version, verdictSpeech);
                }
            }
        }

        async Task<bool> SpeakVerdictAsync(string text, int version, IWorkshopSpeechProvider replySpeech)
        {
            // Drain the held closing clauses first. Guard continues polling
            // authority while this separate playback task waits.
            if (Volatile.Read(ref heldSpeech) is { } closing) await closing.WaitAsync(token);
            await requireAuthority(token);
            if (!await SnapshotAsync(new("assistant"), text, true, token, () => NoPendingInput() && replySpeech.IsCurrentReply)) return false;
            await playback.SpeakAsync(text, version, replySpeech: replySpeech);
            await replySpeech.CompleteReplyAsync(token);
            return true;
        }

        bool NoPendingInput() { lock (inputState) { return !inputPending; } }

        async Task<bool> SnapshotAsync(TranscriptTurn turn, string text, bool final, CancellationToken cancellation,
            Func<bool>? current = null)
        {
            if (text.Length > 1800) { throw new InvalidDataException("Workshop transcript exceeded its bound."); }
            await transcriptGate.WaitAsync(cancellation);
            try
            {
                if (current is not null && !current()) { return false; }
                if (turn.Final || turn.Sequence > 0 && turn.Text == text && !final) { return true; }
                if (turn.Sequence == 0)
                {
                    turn.Sequence = ++transcriptSequence;
                    if (turn.Sequence > 9007199254740991L) { throw new InvalidDataException("Workshop transcript sequence exceeded its bound."); }
                }
                if (turn.Sequence < newestTranscriptSequence) { return false; }
                await sender.JsonAsync(new { type = "workshop_transcript", role = turn.Role, sequence = turn.Sequence, text, final }, cancellation);
                newestTranscriptSequence = turn.Sequence;
                turn.Text = text;
                turn.Final = final;
                return true;
            }
            finally { transcriptGate.Release(); }
        }
    }

    // Cancellation and version checks also cover Stop while GPT is still thinking:
    // a late reply can finish manufacture but cannot restart canceled playback.
    internal sealed class WorkshopPlayback(IWorkshopSpeechProvider speech, LabSocketSender sender, CancellationToken token)
    {
        private readonly object sync = new();
        private readonly SemaphoreSlim serial = new(1, 1);
        private CancellationTokenSource? current;
        private int version;
        internal int Version { get { lock (sync) { return version; } } }
        internal void Interrupt()
        {
            lock (sync) { version++; current?.Cancel(); }
        }
        internal async Task SpeakAsync(string text, int expectedVersion, string? previousText = null,
            IWorkshopSpeechProvider? replySpeech = null)
        {
            await serial.WaitAsync(token);
            try
            {
                using var playback = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (sync)
                {
                    if (version != expectedVersion) { return; }
                    current = playback;
                }
                try
                {
                    await (replySpeech ?? speech).SpeakAsync(text, sender.AudioAsync, playback.Token, previousText);
                    await sender.JsonAsync(new { type = "audio_pause" }, playback.Token);
                }
                catch (OperationCanceledException) when (playback.IsCancellationRequested) { }
                finally { lock (sync) { current = null; } }
            }
            finally { serial.Release(); }
        }
    }

    private sealed class TranscriptTurn(string role)
    {
        internal string Role { get; } = role;
        internal long Sequence;
        internal string Text = "";
        internal bool Final;
    }
}
