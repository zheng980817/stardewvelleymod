using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Pathfinding;
using xTile.ObjectModel;

namespace SleepInNPCBeds
{
    /// <summary>
    /// Lets the player sleep in any NPC's bed. The vanilla sleep flow ends the day and saves,
    /// so this mod only needs to mark the NPC bed tiles as sleepable beds.
    /// </summary>
    public class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>Bed tiles to mark as sleepable, keyed by location (map) name.</summary>
        private readonly Dictionary<string, HashSet<Point>> BedsByLocation =
            new Dictionary<string, HashSet<Point>>(StringComparer.OrdinalIgnoreCase);

        /*********
        ** Public methods
        *********/
        public override void Entry(IModHelper helper)
        {
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.World.LocationListChanged += this.OnLocationListChanged;
            helper.Events.Content.AssetRequested += this.OnAssetRequested;
        }

        /*********
        ** Private methods
        *********/
        /// <summary>When a save loads, find all NPC beds and apply them to the already-loaded maps.</summary>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            this.RebuildBedTable();
            this.ApplyToLoadedMaps();
        }

        /// <summary>Rebuild the bed table daily (cheap), in case modded schedules changed.</summary>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.RebuildBedTable();
            this.ApplyToLoadedMaps();
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            this.BedsByLocation.Clear();
        }

        /// <summary>Apply bed properties to locations added while the game is running.</summary>
        private void OnLocationListChanged(object sender, LocationListChangedEventArgs e)
        {
            if (!Context.IsWorldReady || this.BedsByLocation.Count == 0)
                return;

            foreach (GameLocation location in e.Added)
                this.ApplyToMap(location, location.NameOrUniqueName);
        }

        /// <summary>Apply bed tile properties whenever the game (re)loads a map asset, so reloads keep working.</summary>
        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            if (this.BedsByLocation.Count == 0 || !e.Name.IsDirectlyUnderPath("Maps"))
                return;

            string locationName = e.Name.BaseName.Split('/').Last();
            if (!this.BedsByLocation.TryGetValue(locationName, out HashSet<Point> tiles))
                return;

            e.Edit(
                asset =>
                {
                    xTile.Map map = asset.AsMap().Data;
                    this.MarkBedTiles(map, tiles, locationName);
                },
                AssetEditPriority.Default
            );
        }

        /// <summary>Work out where every NPC's bed is from their schedule data, and cache it.</summary>
        private void RebuildBedTable()
        {
            if (!Context.IsWorldReady)
                return;

            this.BedsByLocation.Clear();

            foreach (NPC npc in Game1.locations.SelectMany(location => location.characters))
            {
                if (npc == null || npc.isMarried())
                    continue; // spouses sleep in the farmhouse bed, which already works in vanilla

                string scheduleKey = npc.hasMasterScheduleEntry("default")
                    ? "default"
                    : npc.hasMasterScheduleEntry("spring")
                        ? "spring"
                        : null;
                if (scheduleKey == null)
                    continue;

                string rawSchedule = npc.getMasterScheduleEntry(scheduleKey);
                if (rawSchedule == null)
                    continue;

                Dictionary<int, SchedulePathDescription> schedule;
                try
                {
                    schedule = npc.parseMasterSchedule(scheduleKey, rawSchedule);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Couldn't parse {npc.Name}'s schedule to find their bed: {ex.Message}", LogLevel.Debug);
                    continue;
                }

                if (schedule == null || schedule.Count == 0)
                    continue;

                // The last stop of the day is where the NPC sleeps. The game marks those routes
                // with a "<npc>_sleep" end-of-route behavior, which is the reliable "this is a bed" marker.
                SchedulePathDescription bed = schedule.OrderBy(entry => entry.Key).Last().Value;
                if (bed == null || bed.targetLocationName == null)
                    continue;

                bool isBedStop = bed.endOfRouteBehavior != null
                    && bed.endOfRouteBehavior.EndsWith("_sleep", StringComparison.OrdinalIgnoreCase);
                if (!isBedStop)
                    continue;

                string locationName = bed.targetLocationName;
                if (locationName.Equals("BusStop", StringComparison.OrdinalIgnoreCase))
                    continue; // married-NPC fallback position, not a real bed

                // Pam's trailer is replaced with Trailer_Big after the community upgrade.
                if (locationName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
                    && Game1.MasterPlayer.mailReceived.Contains("pamHouseUpgrade"))
                {
                    locationName = "Trailer_Big";
                }

                if (!this.BedsByLocation.TryGetValue(locationName, out HashSet<Point> tiles))
                    this.BedsByLocation[locationName] = tiles = new HashSet<Point>();

                tiles.Add(bed.targetTile);
            }

            int total = this.BedsByLocation.Sum(pair => pair.Value.Count);
            this.Monitor.Log($"Sleep in NPC Beds: found {total} NPC bed tile(s) in {this.BedsByLocation.Count} location(s).", LogLevel.Info);
            foreach (KeyValuePair<string, HashSet<Point>> pair in this.BedsByLocation)
            {
                foreach (Point tile in pair.Value)
                    this.Monitor.Log($"  {pair.Key} -> ({tile.X}, {tile.Y})", LogLevel.Debug);
            }
        }

        /// <summary>Apply the cached bed tile properties to every map that is already loaded.</summary>
        private void ApplyToLoadedMaps()
        {
            foreach (GameLocation location in Game1.locations)
                this.ApplyToMap(location, location.NameOrUniqueName);
        }

        /// <summary>Mark the cached NPC bed tiles in an already-loaded location as sleepable.</summary>
        private void ApplyToMap(GameLocation location, string locationName)
        {
            if (location?.map == null || !this.BedsByLocation.TryGetValue(locationName, out HashSet<Point> tiles))
                return;

            this.MarkBedTiles(location.map, tiles, locationName);
        }

        /// <summary>
        /// Add the two vanilla properties that make a tile work like a farmhouse bed:
        /// "Bed T" (the player counts as being in bed and wakes up here) and
        /// "TouchAction Sleep" (stepping on the tile asks "Go to sleep?" and runs the vanilla sleep/save flow).
        /// </summary>
        private void MarkBedTiles(xTile.Map map, IEnumerable<Point> tiles, string locationName)
        {
            xTile.Layers.Layer back = map?.GetLayer("Back");
            if (back == null)
                return;

            foreach (Point tile in tiles)
            {
                xTile.Tiles.Tile mapTile = back.Tiles[tile.X, tile.Y];
                if (mapTile == null)
                {
                    this.Monitor.Log($"No map tile at ({tile.X}, {tile.Y}) in {locationName}; skipped.", LogLevel.Debug);
                    continue;
                }

                if (!mapTile.Properties.ContainsKey("Bed"))
                    mapTile.Properties["Bed"] = new PropertyValue("T");

                if (mapTile.Properties.TryGetValue("TouchAction", out PropertyValue existing))
                {
                    if (existing.ToString() != "Sleep")
                        this.Monitor.Log($"Tile ({tile.X}, {tile.Y}) in {locationName} already has TouchAction '{existing}'; skipped.", LogLevel.Debug);
                }
                else
                {
                    mapTile.Properties["TouchAction"] = new PropertyValue("Sleep");
                }
            }
        }
    }
}
