#region Copyright & License Information
/*
 * Copyright 2021 The OpenHV Developers (see AUTHORS)
 * This file is part of OpenHV, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.HV.Traits
{
	class CustomBaseBuilderQueueManager
	{
		readonly string category;

		readonly CustomBaseBuilderBotModule baseBuilder;
		readonly World world;
		readonly Player player;
		readonly PowerManager powerManager;
		readonly PlayerResources playerResources;
		readonly IResourceLayer resourceLayer;
		readonly BuildingInfluence buildingInfluence;

		int waitTicks;
		Actor[] playerBuildings;
		int failCount;
		int failRetryTicks;
		int checkForBasesTicks;
		int cachedBases;
		int cachedBuildings;
		int minimumExcessPower;

		WaterCheck waterState = WaterCheck.NotChecked;

		public CustomBaseBuilderQueueManager(CustomBaseBuilderBotModule baseBuilder, string category, Player player,
			PowerManager powerManager, PlayerResources playerResources, IResourceLayer resourceLayer)
		{
			this.baseBuilder = baseBuilder;
			world = player.World;
			this.player = player;
			this.powerManager = powerManager;
			this.playerResources = playerResources;
			this.resourceLayer = resourceLayer;
			this.category = category;
			failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
			minimumExcessPower = baseBuilder.Info.MinimumExcessPower;
			if (!baseBuilder.Info.NavalProductionTypes.Any())
				waterState = WaterCheck.DontCheck;

			buildingInfluence = world.WorldActor.Trait<BuildingInfluence>();
		}

		public void Tick(IBot bot)
		{
			// If failed to place something N consecutive times, wait M ticks until resuming building production
			if (failCount >= baseBuilder.Info.MaximumFailedPlacementAttempts && --failRetryTicks <= 0)
			{
				var currentBuildings = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player);
				var baseProviders = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

				// Only bother resetting failCount if either a) the number of buildings has decreased since last failure M ticks ago,
				// or b) number of BaseProviders (construction yard or similar) has increased since then.
				// Otherwise reset failRetryTicks instead to wait again.
				if (currentBuildings < cachedBuildings || baseProviders > cachedBases)
					failCount = 0;
				else
					failRetryTicks = baseBuilder.Info.StructureProductionResumeDelay;
			}

			if (waterState == WaterCheck.NotChecked)
			{
				if (AIUtils.IsAreaAvailable<BaseProvider>(world, player, world.Map, baseBuilder.Info.MaximumBaseRadius, baseBuilder.Info.WaterTerrainTypes))
					waterState = WaterCheck.EnoughWater;
				else
				{
					waterState = WaterCheck.NotEnoughWater;
					checkForBasesTicks = baseBuilder.Info.CheckForNewBasesDelay;
				}
			}

			if (waterState == WaterCheck.NotEnoughWater && --checkForBasesTicks <= 0)
			{
				var currentBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);

				if (currentBases > cachedBases)
				{
					cachedBases = currentBases;
					waterState = WaterCheck.NotChecked;
				}
			}

			// Only update once per second or so
			if (--waitTicks > 0)
				return;

			playerBuildings = world.ActorsHavingTrait<Building>().Where(a => a.Owner == player).ToArray();
			var excessPowerBonus = baseBuilder.Info.ExcessPowerIncrement * (playerBuildings.Length / baseBuilder.Info.ExcessPowerIncreaseThreshold.Clamp(1, int.MaxValue));
			minimumExcessPower = (baseBuilder.Info.MinimumExcessPower + excessPowerBonus).Clamp(baseBuilder.Info.MinimumExcessPower, baseBuilder.Info.MaximumExcessPower);

			var active = false;
			foreach (var queue in AIUtils.FindQueues(player, category))
				if (TickQueue(bot, queue))
					active = true;

			// Add a random factor so not every AI produces at the same tick early in the game.
			// Minimum should not be negative as delays in HackyAI could be zero.
			var randomFactor = world.LocalRandom.Next(0, baseBuilder.Info.StructureProductionRandomBonusDelay);

			waitTicks = active ? baseBuilder.Info.StructureProductionActiveDelay + randomFactor
				: baseBuilder.Info.StructureProductionInactiveDelay + randomFactor;
		}

		bool TickQueue(IBot bot, ProductionQueue queue)
		{
			var currentBuilding = queue.AllQueued().FirstOrDefault();

			// Waiting to build something
			if (currentBuilding == null && failCount < baseBuilder.Info.MaximumFailedPlacementAttempts)
			{
				var item = ChooseBuildingToBuild(queue);
				if (item == null)
					return false;

				bot.QueueOrder(Order.StartProduction(queue.Actor, item.Name, 1));
			}
			else if (currentBuilding != null && currentBuilding.Done)
			{
				// Production is complete
				// Choose the placement logic
				// HACK: HACK HACK HACK
				// TODO: Derive this from BuildingCommonNames instead
				var type = BuildingType.Building;
				CPos? location = null;
				string orderString = "PlaceBuilding";

				// Check if Building is a plug for other Building
				var actorInfo = world.Map.Rules.Actors[currentBuilding.Item];
				var plugInfo = actorInfo.TraitInfoOrDefault<PlugInfo>();
				if (plugInfo != null)
				{
					var possibleBuilding = world.ActorsWithTrait<Pluggable>().FirstOrDefault(a =>
						a.Actor.Owner == player && a.Trait.AcceptsPlug(plugInfo.Type));

					if (possibleBuilding.Actor != null)
					{
						orderString = "PlacePlug";
						location = possibleBuilding.Actor.Location + possibleBuilding.Trait.Info.Offset;
					}
				}
				else
				{
					// Check if Building is a defense and if we should place it towards the enemy or not.
					if (baseBuilder.Info.DefenseTypes.Contains(actorInfo.Name) && world.LocalRandom.Next(100) < baseBuilder.Info.PlaceDefenseTowardsEnemyChance)
						type = BuildingType.Defense;
					else if (baseBuilder.Info.RefineryTypes.Contains(actorInfo.Name))
						type = BuildingType.Refinery;

					location = ChooseBuildLocation(currentBuilding.Item, true, type);
				}

				if (location == null)
				{
					AIUtils.BotDebug($"{player} has nowhere to place {currentBuilding.Item}");
					bot.QueueOrder(Order.CancelProduction(queue.Actor, currentBuilding.Item, 1));
					failCount += failCount;

					// If we just reached the maximum fail count, cache the number of current structures
					if (failCount == baseBuilder.Info.MaximumFailedPlacementAttempts)
					{
						cachedBuildings = world.ActorsHavingTrait<Building>().Count(a => a.Owner == player);
						cachedBases = world.ActorsHavingTrait<BaseProvider>().Count(a => a.Owner == player);
					}
				}
				else
				{
					failCount = 0;

					bot.QueueOrder(new Order(orderString, player.PlayerActor, Target.FromCell(world, location.Value), false)
					{
						// Building to place
						TargetString = currentBuilding.Item,

						// Actor ID to associate the placement with
						ExtraData = queue.Actor.ActorID,
						SuppressVisualFeedback = true
					});

					return true;
				}
			}

			return true;
		}

		ActorInfo GetProducibleBuilding(HashSet<string> actors, IEnumerable<ActorInfo> buildables, Func<ActorInfo, int> orderBy = null)
		{
			var available = buildables.Where(actor =>
			{
				// Are we able to build this?
				if (!actors.Contains(actor.Name))
					return false;

				if (!baseBuilder.Info.BuildingLimits.ContainsKey(actor.Name))
					return true;

				return playerBuildings.Count(a => a.Info.Name == actor.Name) < baseBuilder.Info.BuildingLimits[actor.Name];
			});

			if (orderBy != null)
				return available.MaxByOrDefault(orderBy);

			return available.RandomOrDefault(world.LocalRandom);
		}

		bool HasSufficientPowerForActor(ActorInfo actorInfo)
		{
			return powerManager == null || (actorInfo.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault)
				.Sum(p => p.Amount) + powerManager.ExcessPower) >= baseBuilder.Info.MinimumExcessPower;
		}

		ActorInfo ChooseBuildingToBuild(ProductionQueue queue)
		{
			var buildableThings = queue.BuildableItems();

			// This gets used quite a bit, so let's cache it here
			var power = GetProducibleBuilding(baseBuilder.Info.PowerTypes, buildableThings,
				a => a.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount));

			// First priority is to get out of a low power situation
			if (powerManager != null && powerManager.ExcessPower < minimumExcessPower)
			{
				if (power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(p => p.Amount) > 0)
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (low power)");
					return power;
				}
			}

			// Next is to build up a strong economy
			if (!baseBuilder.HasAdequateRefineryCount)
			{
				var refinery = GetProducibleBuilding(baseBuilder.Info.RefineryTypes, buildableThings);
				if (refinery != null && HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {refinery.Name}: Priority override (refinery)");
					return refinery;
				}

				if (power != null && refinery != null && !HasSufficientPowerForActor(refinery))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (would be low power)");
					return power;
				}
			}

			// Make sure that we can spend as fast as we are earning
			if (baseBuilder.Info.NewProductionCashThreshold > 0 && playerResources.Resources > baseBuilder.Info.NewProductionCashThreshold)
			{
				var production = GetProducibleBuilding(baseBuilder.Info.ProductionTypes, buildableThings);
				if (production != null && HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {production.Name}: Priority override (production)");
					return production;
				}

				if (power != null && production != null && !HasSufficientPowerForActor(production))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (would be low power)");
					return power;
				}
			}

			// Only consider building this if there is enough water inside the base perimeter and there are close enough adjacent buildings
			if (waterState == WaterCheck.EnoughWater && baseBuilder.Info.NewProductionCashThreshold > 0
				&& playerResources.Resources > baseBuilder.Info.NewProductionCashThreshold
				&& AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes))
			{
				var navalproduction = GetProducibleBuilding(baseBuilder.Info.NavalProductionTypes, buildableThings);
				if (navalproduction != null && HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {navalproduction.Name}: Priority override (navalproduction)");
					return navalproduction;
				}

				if (power != null && navalproduction != null && !HasSufficientPowerForActor(navalproduction))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (would be low power)");
					return power;
				}
			}

			// Create some head room for resource storage if we really need it
			if (playerResources.Resources > 0.8 * playerResources.ResourceCapacity)
			{
				var silo = GetProducibleBuilding(baseBuilder.Info.SiloTypes, buildableThings);
				if (silo != null && HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {silo.Name}: Priority override (silo)");
					return silo;
				}

				if (power != null && silo != null && !HasSufficientPowerForActor(silo))
				{
					AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (would be low power)");
					return power;
				}
			}

			// Build everything else
			foreach (var frac in baseBuilder.Info.BuildingFractions.Shuffle(world.LocalRandom))
			{
				var name = frac.Key;

				// Does this building have initial delay, if so have we passed it?
				if (baseBuilder.Info.BuildingDelays != null &&
					baseBuilder.Info.BuildingDelays.ContainsKey(name) &&
					baseBuilder.Info.BuildingDelays[name] > world.WorldTick)
					continue;

				// Can we build this structure?
				if (!buildableThings.Any(b => b.Name == name))
					continue;

				// Do we want to build this structure?
				var count = playerBuildings.Count(a => a.Info.Name == name);
				if (count * 100 > frac.Value * playerBuildings.Length)
					continue;

				if (baseBuilder.Info.BuildingLimits.ContainsKey(name) && baseBuilder.Info.BuildingLimits[name] <= count)
					continue;

				// If we're considering to build a naval structure, check whether there is enough water inside the base perimeter
				// and any structure providing buildable area close enough to that water.
				// TODO: Extend this check to cover any naval structure, not just production.
				if (baseBuilder.Info.NavalProductionTypes.Contains(name)
					&& (waterState == WaterCheck.NotEnoughWater
						|| !AIUtils.IsAreaAvailable<GivesBuildableArea>(world, player, world.Map, baseBuilder.Info.CheckForWaterRadius, baseBuilder.Info.WaterTerrainTypes)))
					continue;

				// Will this put us into low power?
				var actor = world.Map.Rules.Actors[name];
				if (powerManager != null && (powerManager.ExcessPower < minimumExcessPower || !HasSufficientPowerForActor(actor)))
				{
					// Try building a power plant instead
					if (power != null && power.TraitInfos<PowerInfo>().Where(i => i.EnabledByDefault).Sum(pi => pi.Amount) > 0)
					{
						if (powerManager.PowerOutageRemainingTicks > 0)
							AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (is low power)");
						else
							AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {power.Name}: Priority override (would be low power)");

						return power;
					}
				}

				// Lets build this
				AIUtils.BotDebug($"{queue.Actor.Owner} decided to build {name}: Desired is {frac.Value} ({frac.Value * playerBuildings.Length} / {playerBuildings.Length}); current is {count} / {playerBuildings.Length}");
				return actor;
			}

			// Too spammy to keep enabled all the time, but very useful when debugging specific issues.
			// AIUtils.BotDebug($"{queue.Actor.Owner} couldn't decide what to build for queue {queue.Info.Type}.");
			return null;
		}

		bool AdjacentBuildingAt(CPos cell)
		{
			foreach (var adjacent in CVec.Directions)
			{
				var adjacentCell = cell + adjacent;
				if (buildingInfluence.AnyBuildingAt(adjacentCell))
					return true;
			}

			return false;
		}

		bool ResourcesNearby(CPos cell, int radius)
		{
			var surroundingCells = world.Map.FindTilesInCircle(cell, radius);
			foreach (var surroundingCell in surroundingCells)
			{
				if (resourceLayer.GetResource(surroundingCell).Density > 0)
					return true;
			}

			return false;
		}

		CPos? ChooseBuildLocation(string actorType, bool distanceToBaseIsImportant, BuildingType type)
		{
			var actorInfo = world.Map.Rules.Actors[actorType];
			var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null)
				return null;

			// Find the buildable cell that is closest to pos and centered around center
			Func<CPos, CPos, int, int, CPos?> findPos = (center, target, minRange, maxRange) =>
			{
				var cells = world.Map.FindTilesInAnnulus(center, minRange, maxRange);

				// Sort by distance to target if we have one
				if (center != target)
					cells = cells.OrderBy(c => (c - target).LengthSquared);
				else
					cells = cells.Shuffle(world.LocalRandom);

				foreach (var cell in cells)
				{
					if (!world.CanPlaceBuilding(cell, actorInfo, buildingInfo, null))
						continue;

					// Don't clutter the base
					if (AdjacentBuildingAt(cell))
						continue;

					// Don't block of resources
					if (ResourcesNearby(cell, baseBuilder.Info.ResourceDistance))
						continue;

					if (distanceToBaseIsImportant && !buildingInfo.IsCloseEnoughToBase(world, player, actorInfo, cell))
						continue;

					return cell;
				}

				return null;
			};

			var baseCenter = baseBuilder.GetRandomBaseCenter();

			switch (type)
			{
				case BuildingType.Defense:
					// Build near the closest enemy structure
					var closestEnemy = world.ActorsHavingTrait<Building>().Where(a => !a.Disposed && player.RelationshipWith(a.Owner) == PlayerRelationship.Enemy)
						.ClosestTo(world.Map.CenterOfCell(baseBuilder.DefenseCenter));

					var targetCell = closestEnemy != null ? closestEnemy.Location : baseCenter;
					return findPos(baseBuilder.DefenseCenter, targetCell, baseBuilder.Info.MinimumDefenseRadius, baseBuilder.Info.MaximumDefenseRadius);

				case BuildingType.Refinery:
					// Try and place the refinery near a resource field
					if (resourceLayer != null)
					{
						var nearbyResources = world.Map.FindTilesInAnnulus(baseCenter, baseBuilder.Info.MinimumBaseRadius, baseBuilder.Info.MaximumBaseRadius)
							.Where(a => resourceLayer.GetResource(a).Type != null)
							.Shuffle(world.LocalRandom).Take(baseBuilder.Info.MaximumResourceCellsToCheck);

						foreach (var resource in nearbyResources)
							return findPos(baseCenter, resource, baseBuilder.Info.MinimumBaseRadius, baseBuilder.Info.MaximumBaseRadius);
					}

					// Try and find a free spot somewhere else in the base
					return findPos(baseCenter, baseCenter, baseBuilder.Info.MinimumBaseRadius, baseBuilder.Info.MaximumBaseRadius);

				case BuildingType.Building:
					return findPos(baseCenter, baseCenter, baseBuilder.Info.MinimumBaseRadius,
						distanceToBaseIsImportant ? baseBuilder.Info.MaximumBaseRadius : world.Map.Grid.MaximumTileSearchRange);
			}

			// Can't find a build location
			return null;
		}
	}
}
