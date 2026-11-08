using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using RimWorld;
using RiMCP.Read;
using Verse;
using Verse.AI;

namespace RiMCP.Command
{
    internal sealed class ColonistState
    {
        public string Id;
        public string Name;
        public bool Dead;
        public bool Downed;
        public bool Bleeding;
        public int Injuries;
        public string MentalState;
        // 0 when fed; 1 when hunger is at Starving; otherwise 1 + the malnutrition stage, so a stage
        // reached is a step up. Hunger says what it is.
        public int Starvation;
        public string Hunger;
        // Unable to reach any bed or food for a sustained time, checked every 250 ticks and only
        // for a run whose stopOn has stuck.
        public bool Stuck;
        public string StuckDetail;
    }

    internal sealed class LetterState
    {
        public int Id;
        public string Label;
        public string DefName;
        public bool Threat;
    }

    internal sealed class AdvanceObservation
    {
        public object Game;
        public bool Playing;
        public int Tick;
        public TimeSpeed Speed;
        // Label of a visible active threat to the colony, or null.
        public string Threat;
        // Label of a home map whose stored food is below the Low food threshold, or null.
        public string LowFood;
        // The fire episode's level (see FireWatch), and where the largest fire on the ground in a
        // home map's Home area burns now.
        public int FireLevel;
        public string Fire;
        public readonly Dictionary<string, ColonistState> Colonists = new Dictionary<string, ColonistState>();
        public readonly Dictionary<int, LetterState> Letters = new Dictionary<int, LetterState>();
    }

    internal sealed class AdvanceEvent
    {
        public string Kind;
        public int Tick;
        public string PawnId;
        public string PawnName;
        public string Detail;
    }

    // One advance_time request. Step only compares observations, so the lifecycle can be tested
    // without a running game; AdvanceRunner gathers observations and applies side effects.
    internal sealed class AdvanceRun
    {
        public const string Running = "running";
        public const int MaxEvents = 50;
        // Also the order in which simultaneous events are reported.
        public static readonly string[] Conditions = { "death", "downed", "injured", "starving", "stuck", "mentalBreak", "fire", "hostiles", "lowFood", "threatLetter", "letter" };
        // Everything a player stops for, which is all but letter: fire is a fire on the ground in a
        // home map's Home area (the game's Fire in home area alert also counts other maps' and burning
        // pawns), starving is the game's Starvation alert, and stuck is a colonist the game does not
        // report.
        public static readonly string[] DefaultStopOn = { "death", "downed", "injured", "starving", "stuck", "mentalBreak", "fire", "hostiles", "lowFood", "threatLetter" };
        // Fire cells burning in a Home area at which fire stops a run again as it grows.
        public static readonly int[] FireSizes = { 1, 10, 50 };

        public readonly int Id;
        public readonly string RequestId;
        public readonly object Game;
        public readonly TimeSpeed Speed;
        public readonly int StartTick;
        public readonly int TargetTick;
        public readonly float Hours;
        public readonly HashSet<string> StopOn;
        public readonly List<AdvanceEvent> Events = new List<AdvanceEvent>();
        public readonly ManualResetEventSlim Ended = new ManualResetEventSlim(false);
        public int DroppedEvents;
        public string Status = Running;
        public string EndReason;
        public AdvanceEvent StoppedBy;
        public int EndTick = -1;
        public AdvanceObservation Previous;
        public int Frames;
        public double FrameMsTotal;
        public double FrameMsMax;
        public bool Failed;
        public bool Revoked;

        public AdvanceRun(int id, string requestId, object game, TimeSpeed speed, int startTick, int targetTick, float hours, IEnumerable<string> stopOn, AdvanceObservation baseline)
        {
            Id = id;
            RequestId = requestId;
            Game = game;
            Speed = speed;
            StartTick = startTick;
            TargetTick = targetTick;
            Hours = hours;
            StopOn = new HashSet<string>(stopOn);
            Previous = baseline;
        }

        public void Step(AdvanceObservation current)
        {
            if (Status != Running)
            {
                return;
            }
            if (!current.Playing || !ReferenceEquals(current.Game, Game))
            {
                End("interrupted", "gameUnloaded", Previous.Tick);
                return;
            }

            List<AdvanceEvent> changes = Detect(Previous, current);
            foreach (AdvanceEvent change in changes)
            {
                Record(change);
            }
            Previous = current;

            // A stop condition outranks reaching the target and a pause in the same frame: the
            // game pauses itself for some letters, and the agent needs the cause.
            AdvanceEvent stop = changes.FirstOrDefault(StopsRun);
            if (stop != null)
            {
                StoppedBy = stop;
                End("stopped", stop.Kind, current.Tick);
            }
            else if (current.Tick >= TargetTick)
            {
                End("completed", "completed", current.Tick);
            }
            else if (current.Speed != Speed)
            {
                End("interrupted", current.Speed == TimeSpeed.Paused ? "paused" : "speedChanged", current.Tick);
            }
        }

        public bool SameRequest(float hours, TimeSpeed speed, ICollection<string> stopOn)
        {
            return Hours == hours && Speed == speed && StopOn.SetEquals(stopOn);
        }

        public void RecordFrame(double milliseconds)
        {
            Frames++;
            FrameMsTotal += milliseconds;
            FrameMsMax = Math.Max(FrameMsMax, milliseconds);
        }

        // The run owns time until it ends: completing, stopping, or failing pauses the game. A pause
        // or speed change by someone else is left alone.
        public bool PausesGameAtEnd
        {
            get { return Failed || Revoked || Status == "stopped" || Status == "completed"; }
        }

        public void Revoke(string reason, int tick)
        {
            Revoked = true;
            End("interrupted", reason, tick);
        }

        public void Fail(string message, int tick)
        {
            Failed = true;
            End("interrupted", "error: " + message, tick);
        }

        public void End(string status, string reason, int tick)
        {
            Status = status;
            EndReason = reason;
            EndTick = tick;
        }

        private bool StopsRun(AdvanceEvent change)
        {
            return StopOn.Contains(change.Kind) || change.Kind == "threatLetter" && StopOn.Contains("letter");
        }

        private void Record(AdvanceEvent change)
        {
            if (Events.Count >= MaxEvents)
            {
                DroppedEvents++;
                return;
            }
            Events.Add(change);
        }

        // Changes since the previous frame. Comparing frames rather than the start means a colonist
        // who is rescued and downed again stops the run again.
        public static List<AdvanceEvent> Detect(AdvanceObservation previous, AdvanceObservation current)
        {
            List<AdvanceEvent> changes = new List<AdvanceEvent>();
            foreach (ColonistState now in current.Colonists.Values)
            {
                ColonistState before;
                if (!previous.Colonists.TryGetValue(now.Id, out before) || before.Dead)
                {
                    continue;
                }
                if (now.Dead)
                {
                    changes.Add(PawnEvent("death", current.Tick, now, "died"));
                    continue;
                }
                if (now.Downed && !before.Downed)
                {
                    changes.Add(PawnEvent("downed", current.Tick, now, "downed"));
                }
                if (now.Injuries > before.Injuries || now.Bleeding && !before.Bleeding)
                {
                    changes.Add(PawnEvent("injured", current.Tick, now, now.Bleeding ? "bleeding" : "new injury"));
                }
                if (now.Starvation > before.Starvation)
                {
                    changes.Add(PawnEvent("starving", current.Tick, now, now.Hunger));
                }
                if (now.Stuck && !before.Stuck)
                {
                    changes.Add(PawnEvent("stuck", current.Tick, now, now.StuckDetail));
                }
                if (now.MentalState != null && before.MentalState == null)
                {
                    changes.Add(PawnEvent("mentalBreak", current.Tick, now, now.MentalState));
                }
            }
            // A fire that starts, and the same fire growing past each of FireSizes.
            if (current.FireLevel > previous.FireLevel)
            {
                changes.Add(new AdvanceEvent { Kind = "fire", Tick = current.Tick, Detail = current.Fire });
            }
            if (current.Threat != null && previous.Threat == null)
            {
                changes.Add(new AdvanceEvent { Kind = "hostiles", Tick = current.Tick, Detail = current.Threat });
            }
            if (current.LowFood != null && previous.LowFood == null)
            {
                changes.Add(new AdvanceEvent { Kind = "lowFood", Tick = current.Tick, Detail = current.LowFood });
            }
            foreach (LetterState letter in current.Letters.Values.OrderBy(letter => letter.Id))
            {
                if (!previous.Letters.ContainsKey(letter.Id))
                {
                    changes.Add(new AdvanceEvent { Kind = letter.Threat ? "threatLetter" : "letter", Tick = current.Tick, Detail = letter.Label });
                }
            }
            return changes.OrderBy(change => Array.IndexOf(Conditions, change.Kind)).ToList();
        }

        private static AdvanceEvent PawnEvent(string kind, int tick, ColonistState pawn, string detail)
        {
            return new AdvanceEvent { Kind = kind, Tick = tick, PawnId = pawn.Id, PawnName = pawn.Name, Detail = detail };
        }

        // How many of FireSizes a fire of this many cells has reached.
        public static int FireLevel(int cells)
        {
            return FireSizes.Count(size => cells >= size);
        }
    }

    // One fire episode in the Home areas: the highest of FireSizes reached since fire last burned
    // there, so a fire that shrinks and grows again while colonists fight it, or a run that starts
    // during it, stops a run once for each size. The episode ends only once no fire has burned in a
    // Home area for QuietTicks, so a fire flickering out and back is still the same fire.
    internal sealed class FireWatch
    {
        public const int QuietTicks = 4 * GenTicks.TickRareInterval;

        public int Level;
        private int quietSince = -1;

        public int Note(int tick, int cells)
        {
            if (cells > 0)
            {
                quietSince = -1;
                Level = Math.Max(Level, AdvanceRun.FireLevel(cells));
            }
            else if (Level > 0)
            {
                if (quietSince < 0 || tick < quietSince)
                {
                    quietSince = tick;
                }
                else if (tick - quietSince >= QuietTicks)
                {
                    Level = 0;
                    quietSince = -1;
                }
            }
            return Level;
        }
    }

    // Whether one colonist has been trapped long enough to count as stuck. Each check says whether
    // the colonist is trapped now: awake, undrafted, not downed, on a home map, and unable to reach
    // any of its beds or food. Asleep, drafted, downed, or able to reach one starts it over, and so
    // does a gap between checks longer than they come during a run, or time going back. The timers
    // start over with each run, so the hour is always counted within a run.
    internal sealed class StuckTimer
    {
        public const int SustainTicks = GenDate.TicksPerHour;
        public const int MaxGapTicks = 4 * GenTicks.TickRareInterval;

        public int Since = -1;
        public int LastTick = int.MinValue;
        public bool Stuck;
        public string Detail;

        public bool Note(int tick, bool trapped, string where)
        {
            if (!trapped)
            {
                Since = -1;
            }
            else if (Since < 0 || tick < LastTick || tick - LastTick > MaxGapTicks)
            {
                Since = tick;
            }
            LastTick = tick;
            Stuck = trapped && tick - Since >= SustainTicks;
            Detail = Stuck ? where + " for " + ((double)(tick - Since) / GenDate.TicksPerHour).ToString("0.0", CultureInfo.InvariantCulture) + " in-game hours" : null;
            return Stuck;
        }

        // The targets a trapped check tries: one per place (a district, where reaching one cell
        // reaches them all), nearest first, at most cap. A target with no place of its own, such
        // as food on a shelf, is its own.
        public static List<T> Nearest<T>(IEnumerable<T> targets, Func<T, object> place, Func<T, float> distance, int cap)
        {
            HashSet<object> seen = new HashSet<object>();
            List<T> picked = new List<T>();
            foreach (T target in targets.OrderBy(distance))
            {
                object key = place(target);
                if (key == null || seen.Add(key))
                {
                    picked.Add(target);
                    if (picked.Count >= cap)
                    {
                        break;
                    }
                }
            }
            return picked;
        }
    }

    // Owns advance runs. Everything here runs on the main thread; HTTP threads only wait on a
    // run's Ended event.
    internal static class AdvanceRunner
    {
        // Retries are recognised for this many recent runs of the loaded game.
        public const int MaxHistory = 32;

        // Runs of the loaded game, oldest first.
        private static readonly List<AdvanceRun> history = new List<AdvanceRun>();
        private static AdvanceRun latest;
        private static int nextId;
        private static List<Pawn> tracked = new List<Pawn>();
        // Summing stored food costs too much for every frame, so it is checked every 250 ticks and
        // carried between checks.
        private const int FoodCheckInterval = GenTicks.TickRareInterval;
        private static int foodCheckedTick = int.MinValue;
        private static string lowFood;
        // Reachability costs too much for every frame too, so colonists are checked for being trapped
        // every 250 ticks, and only for a run whose stopOn has stuck; each run starts their timers
        // over. A check tries one target per place, nearest first, up to StuckTargets.
        private const int StuckCheckInterval = GenTicks.TickRareInterval;
        private const int StuckTargets = 24;
        private static int stuckCheckedTick = int.MinValue;
        private static readonly Dictionary<Pawn, StuckTimer> stuck = new Dictionary<Pawn, StuckTimer>();
        private static bool watchStuck;
        // The fire episode carries from run to run within one loaded game.
        private static FireWatch fire = new FireWatch();
        private static object fireGame;

        // Replaceable so tests can simulate an observation failure.
        internal static Func<AdvanceObservation> Observer = Observe;

        // Whether an advance is running now.
        public static bool Running
        {
            get { return latest != null && latest.Status == AdvanceRun.Running; }
        }

        public static AdvanceRun LatestInLoadedGame
        {
            get { return latest != null && ReferenceEquals(latest.Game, Current.Game) ? latest : null; }
        }

        public static AdvanceRun Recall(string requestId)
        {
            return requestId == null ? null : history.LastOrDefault(run => run.RequestId == requestId && ReferenceEquals(run.Game, Current.Game));
        }

        public static AdvanceRun RunById(int id)
        {
            return history.FirstOrDefault(run => run.Id == id && ReferenceEquals(run.Game, Current.Game));
        }

        public static AdvanceRun Start(float hours, TimeSpeed speed, ICollection<string> stopOn, string requestId, out bool created)
        {
            history.RemoveAll(run => !ReferenceEquals(run.Game, Current.Game));
            AdvanceRun receipt = FindReceipt(history, Current.Game, requestId, hours, speed, stopOn);
            if (receipt != null)
            {
                created = false;
                return receipt;
            }
            if (latest != null && latest.Status == AdvanceRun.Running && ReferenceEquals(latest.Game, Current.Game))
            {
                throw new CommandException(409, "An advance is already running (run " + latest.Id + "). Use get_advance to wait for it, or set_game_speed to stop it.");
            }

            // Take the starting snapshot before unpausing, so a failure here leaves the game as it was.
            tracked = new List<Pawn>();
            foodCheckedTick = int.MinValue;
            stuckCheckedTick = int.MinValue;
            stuck.Clear();
            watchStuck = stopOn.Contains("stuck");
            AdvanceObservation baseline = Observe();
            TickManager ticks = Find.TickManager;
            ticks.CurTimeSpeed = speed;
            if (ticks.CurTimeSpeed != speed)
            {
                throw new CommandException(409, "RimWorld is not accepting time control right now.");
            }

            int duration = (int)Math.Ceiling(hours * GenDate.TicksPerHour);
            AdvanceRun run = new AdvanceRun(++nextId, requestId, Current.Game, speed, baseline.Tick, baseline.Tick + duration, hours, stopOn, baseline);
            history.Add(run);
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(0);
            }
            latest = run;
            created = true;
            return run;
        }

        // A repeated requestId returns its run; the same id with different arguments is a client error.
        public static AdvanceRun FindReceipt(IEnumerable<AdvanceRun> runs, object game, string requestId, float hours, TimeSpeed speed, ICollection<string> stopOn)
        {
            if (requestId == null)
            {
                return null;
            }
            AdvanceRun run = runs.LastOrDefault(item => item.RequestId == requestId && ReferenceEquals(item.Game, game));
            if (run != null && !run.SameRequest(hours, speed, stopOn))
            {
                throw new CommandException(409, "requestId '" + requestId + "' was already used for run " + run.Id + " with different arguments.");
            }
            return run;
        }

        public static AdvanceRun Interrupt(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            run.End("interrupted", reason, Current.Game == null ? run.Previous.Tick : Find.TickManager.TicksGame);
            run.Ended.Set();
            return run;
        }

        // A dialog pauses the game without changing its speed, so a run would resume once it closed.
        public static AdvanceRun EndForDialog(string reason)
        {
            AdvanceRun run = Interrupt(reason);
            if (run != null && Current.Game != null && ReferenceEquals(Current.Game, run.Game))
            {
                Find.TickManager.Pause();
            }
            return run;
        }

        // The agent lost time control while its run owned time: end the run and pause.
        public static AdvanceRun Revoke(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            bool loaded = Current.Game != null && ReferenceEquals(Current.Game, run.Game);
            run.Revoke(reason, loaded ? Find.TickManager.TicksGame : run.Previous.Tick);
            if (loaded)
            {
                Find.TickManager.Pause();
            }
            run.Ended.Set();
            return run;
        }

        public static void Update()
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return;
            }

            Stopwatch frame = Stopwatch.StartNew();
            try
            {
                run.Step(Observer());
            }
            catch (Exception ex)
            {
                run.Fail(ex.GetType().Name + ": " + ex.Message, run.Previous.Tick);
            }
            run.RecordFrame(frame.Elapsed.TotalMilliseconds);
            if (run.Status == AdvanceRun.Running)
            {
                return;
            }

            if (run.PausesGameAtEnd && Current.Game != null && ReferenceEquals(Current.Game, run.Game))
            {
                Find.TickManager.Pause();
            }
            run.Ended.Set();
        }

        private static AdvanceObservation Observe()
        {
            AdvanceObservation observation = new AdvanceObservation
            {
                Game = Current.Game,
                Playing = Current.Game != null && Current.ProgramState == ProgramState.Playing && Find.TickManager != null
            };
            if (!observation.Playing)
            {
                return observation;
            }

            observation.Tick = Find.TickManager.TicksGame;
            observation.Speed = Find.TickManager.CurTimeSpeed;
            List<Pawn> pawns = new List<Pawn>();
            foreach (Map map in Find.Maps)
            {
                pawns.AddRange(map.mapPawns.FreeColonistsSpawned);
                IAttackTarget threat;
                if (observation.Threat == null && GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer, out threat))
                {
                    observation.Threat = ThreatLabel(threat.Thing);
                }
            }
            if (observation.Tick < foodCheckedTick || observation.Tick >= foodCheckedTick + FoodCheckInterval)
            {
                foodCheckedTick = observation.Tick;
                Map low = Find.Maps.FirstOrDefault(FoodSummary.IsLow);
                lowFood = low == null ? null : "stored food on " + (low.Parent == null ? "map " + low.uniqueID : low.Parent.LabelCap) + " is below " + FoodSummary.LowPerColonist + " nutrition per colonist";
            }
            observation.LowFood = lowFood;
            ObserveFire(observation);
            if (watchStuck && (observation.Tick < stuckCheckedTick || observation.Tick >= stuckCheckedTick + StuckCheckInterval))
            {
                stuckCheckedTick = observation.Tick;
                CheckStuck(pawns, observation.Tick);
            }
            // Colonists who died since the last frame are no longer spawned colonists.
            pawns.AddRange(tracked.Where(pawn => pawn.Dead && !pawns.Contains(pawn)));
            tracked = pawns.Where(pawn => !pawn.Dead).ToList();

            foreach (Pawn pawn in pawns)
            {
                observation.Colonists[pawn.ThingID] = Colonist(pawn);
            }
            foreach (Letter letter in Find.LetterStack.LettersListForReading)
            {
                observation.Letters[letter.ID] = new LetterState
                {
                    Id = letter.ID,
                    Label = letter.Label.ToString().StripTags(),
                    DefName = letter.def == null ? null : letter.def.defName,
                    Threat = letter.def != null && (letter.def == LetterDefOf.ThreatBig || letter.def == LetterDefOf.ThreatSmall)
                };
            }
            return observation;
        }

        // Cells burning on the ground in the Home area of each home map (the player's settlements),
        // unfogged ones only, where colonists fight fires; fires on pawns, such as a burning
        // colonist, are left to injured. Maps that are not homes are not watched. The largest is
        // reported, and the episode's level is what stops a run.
        private static void ObserveFire(AdvanceObservation observation)
        {
            if (!ReferenceEquals(fireGame, Current.Game))
            {
                fire = new FireWatch();
                fireGame = Current.Game;
            }
            int largest = 0;
            string where = null;
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome)
                {
                    continue;
                }
                List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
                if (fires.Count == 0)
                {
                    continue;
                }
                Area_Home home = map.areaManager.Home;
                int cells = 0;
                Thing first = null;
                foreach (Thing burning in fires)
                {
                    Fire ground = burning as Fire;
                    if (home != null && (ground == null || !(ground.parent is Pawn)) && home[burning.Position] && !burning.Position.Fogged(map))
                    {
                        cells++;
                        first = first ?? burning;
                    }
                }
                if (cells > largest)
                {
                    largest = cells;
                    where = cells + (cells == 1 ? " cell" : " cells") + " burning in the Home area of " + MapLabel(map) + ", one at " + first.Position.x + "," + first.Position.z;
                }
            }
            observation.FireLevel = fire.Note(observation.Tick, largest);
            observation.Fire = where;
        }

        // Each colonist trapped now: awake, undrafted and not downed on a home map that has beds or
        // food, and unable to reach any of them. The timers decide when that has lasted.
        private static void CheckStuck(List<Pawn> pawns, int tick)
        {
            foreach (Pawn gone in stuck.Keys.Where(pawn => !pawns.Contains(pawn)).ToList())
            {
                stuck.Remove(gone);
            }
            Dictionary<Map, List<KeyValuePair<Thing, District>>> targets = new Dictionary<Map, List<KeyValuePair<Thing, District>>>();
            foreach (Pawn pawn in pawns)
            {
                StuckTimer timer;
                if (!stuck.TryGetValue(pawn, out timer))
                {
                    stuck[pawn] = timer = new StuckTimer();
                }
                string where = null;
                bool trapped = false;
                if (pawn.Spawned && pawn.Map.IsPlayerHome && !pawn.Dead && !pawn.Downed && !pawn.Drafted && pawn.Awake())
                {
                    List<KeyValuePair<Thing, District>> places;
                    if (!targets.TryGetValue(pawn.Map, out places))
                    {
                        targets[pawn.Map] = places = BedsAndFood(pawn.Map);
                    }
                    IntVec3 from = pawn.Position;
                    List<KeyValuePair<Thing, District>> tried = StuckTimer.Nearest(places, place => place.Value, place => (place.Key.Position - from).LengthHorizontalSquared, StuckTargets);
                    trapped = tried.Count > 0 && !tried.Any(place => pawn.CanReach(place.Key, PathEndMode.Touch, Danger.Deadly));
                    if (trapped)
                    {
                        where = "cannot reach any bed or food from " + pawn.Position.x + "," + pawn.Position.z;
                    }
                }
                timer.Note(tick, trapped, where);
            }
        }

        // The colony's beds for people and the food people eat, unfogged, each with the district it
        // is in, null where it has none, such as food on a shelf.
        private static List<KeyValuePair<Thing, District>> BedsAndFood(Map map)
        {
            List<Thing> targets = map.listerBuildings.allBuildingsColonist
                .OfType<Building_Bed>()
                .Where(bed => bed.def.building != null && bed.def.building.bed_humanlike)
                .Cast<Thing>()
                .ToList();
            targets.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree)
                .Where(food => !(food is Corpse) && (food.def.ingestible == null || food.def.ingestible.HumanEdible) && !food.Position.Fogged(map)));
            return targets.Select(target => new KeyValuePair<Thing, District>(target, target.Position.GetDistrict(map))).ToList();
        }

        private static string MapLabel(Map map)
        {
            return map.Parent == null ? "map " + map.uniqueID : map.Parent.LabelCap.ToString();
        }

        // Labels carry rich-text markup, such as the coloured title after a raider's name.
        private static string ThreatLabel(Thing thing)
        {
            if (thing == null)
            {
                return "hostiles";
            }
            Pawn pawn = thing as Pawn;
            return (pawn != null ? pawn.LabelShortCap : thing.LabelCap).StripTags();
        }

        private static ColonistState Colonist(Pawn pawn)
        {
            ColonistState state = new ColonistState
            {
                Id = pawn.ThingID,
                Name = ReadUtil.PawnName(pawn),
                Dead = pawn.Dead
            };
            if (pawn.Dead || pawn.health == null || pawn.health.hediffSet == null)
            {
                return state;
            }
            state.Downed = pawn.Downed;
            state.Bleeding = pawn.health.hediffSet.BleedRateTotal > 0f;
            // Fresh injuries only: scars are permanent, and diseases needing re-tending are not new harm.
            state.Injuries = pawn.health.hediffSet.hediffs.Count(hediff => hediff is Hediff_Injury && !hediff.IsPermanent());
            state.MentalState = pawn.MentalStateDef == null ? null : ReadUtil.DefLabel(pawn.MentalStateDef);
            Starvation(pawn, state);
            StuckTimer timer;
            if (stuck.TryGetValue(pawn, out timer) && timer.Stuck)
            {
                state.Stuck = true;
                state.StuckDetail = timer.Detail;
            }
            return state;
        }

        // Hunger at Starving, as the game's Starvation alert finds it, or the malnutrition it leads
        // to, by stage.
        private static void Starvation(Pawn pawn, ColonistState state)
        {
            bool starving = pawn.needs != null && pawn.needs.food != null && pawn.needs.food.Starving;
            Hediff malnutrition = HediffDefOf.Malnutrition == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
            state.Starvation = Math.Max(starving ? 1 : 0, malnutrition == null ? 0 : malnutrition.CurStageIndex + 1);
            if (state.Starvation == 0)
            {
                return;
            }
            List<string> parts = new List<string>();
            if (starving)
            {
                parts.Add("hunger at Starving");
            }
            if (malnutrition != null)
            {
                string stage = malnutrition.CurStage == null || string.IsNullOrEmpty(malnutrition.CurStage.label) ? "" : " " + malnutrition.CurStage.label;
                parts.Add("malnutrition" + stage + " (" + Math.Round(malnutrition.Severity * 100) + "%)");
            }
            state.Hunger = string.Join(", ", parts.ToArray());
        }
    }
}
