/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */
using System;
using DOL.AI.Brain;
using DOL.GS.Database;
using DOL.GS.PacketHandler;
using DOL.GS.Effects;
using DOL.GS.SkillHandler;

namespace DOL.GS.Spells
{
	/// <summary>
	/// Spell Handler for firing bolts
	/// </summary>
	[SpellHandlerAttribute("Bolt")]
	public class BoltSpellHandler : SpellHandler
	{
		/// <summary>
		/// Fire bolt
		/// </summary>
		/// <param name="target"></param>
		public override void FinishSpellCast(GameLivingBase target)
		{
			m_caster.ChangeMana(null, -CalculateNeededPower(target));
			base.FinishSpellCast(target);
		}

		/// <summary>
		/// Calculates chance of spell getting resisted
		/// </summary>
		/// <param name="target">the target of the spell</param>
		/// <returns>chance that spell will be resisted for specific target</returns>
		public override int CalculateSpellResistChance(GameLiving target)
		{
			return 0;
		}

		/// <summary>
		/// Apply effect on target or do spell action if non duration spell
		/// </summary>
		/// <param name="target">target that gets the effect</param>
		/// <param name="effectiveness">factor from 0..1 (0%-100%)</param>
		public override void ApplyEffectOnTarget(GameLiving target, double effectiveness)
		{
			int ticksToTarget = m_caster.Position.GetDistance(target.Position) * 100 / 85; // 85 units per 1/10s
			int delay = 1 + ticksToTarget / 100;
			foreach (GamePlayer player in target.GetInRadius(typeof(GamePlayer), WorldMgr.VISIBILITY_DISTANCE)) 
			{
				player.Out.SendSpellEffectAnimation(m_caster, target, m_spell.ClientEffect, (ushort)(delay), false, 1);
			}
			BoltOnTargetAction bolt = new BoltOnTargetAction(Caster, target, this);
			bolt.Start(1 + ticksToTarget);
		}

		/// <summary>
		/// Delayed action when bolt reach the target
		/// </summary>
		protected class BoltOnTargetAction : RegionAction
		{
			/// <summary>
			/// The bolt target
			/// </summary>
			protected readonly GameLiving m_boltTarget;

			/// <summary>
			/// The spell handler
			/// </summary>
			protected readonly BoltSpellHandler m_handler;

			/// <summary>
			/// Constructs a new BoltOnTargetAction
			/// </summary>
			/// <param name="actionSource">The action source</param>
			/// <param name="boltTarget">The bolt target</param>
			public BoltOnTargetAction(GameLiving actionSource, GameLiving boltTarget, BoltSpellHandler spellHandler) : base(actionSource)
			{
				if (boltTarget == null)
					throw new ArgumentNullException("boltTarget");
				if (spellHandler == null)
					throw new ArgumentNullException("spellHandler");
				m_boltTarget = boltTarget;
				m_handler = spellHandler;
			}

			/// <summary>
			/// Called on every timer tick
			/// </summary>
			protected override void OnTick()
			{
				GameLiving target = m_boltTarget;
				GameLiving caster = (GameLiving)m_actionSource;
				if (target == null) return;
				if (target.Region != caster.Region) return;
				if (target.ObjectState != eObjectState.Active) return;
				if (!target.Alive) return;

// TODO: find out how exactly this works
//				if (target is GamePlayer && target.AttackState && target.InCombat)
//				{
//					MessageToCaster(target.GetName(0, false) + " is in combat.", eChatType.CT_YouHit);
//					return;
//				}

				// Missrate
				int missrate = (caster is GamePlayer) ? 20 : 25;
				if (target is GameNPC || caster is GameNPC)
				{
					missrate += (int)(5 * caster.GetConLevel(target));
				}

				// add defence bonus from last executed style if any
				AttackData targetAD = (AttackData)target.TempProperties.getObjectProperty(GameLiving.LAST_ATTACK_DATA, null);
				if (targetAD != null
					&& targetAD.AttackResult == GameLiving.eAttackResult.HitStyle
					&& targetAD.Style != null)
				{
					missrate += targetAD.Style.BonusToDefense;
				}

				AttackData ad = m_handler.CalculateDamageToTarget(target, 0.5); // half of the damage is magical

				if (Util.Chance(missrate)) 
				{
					ad.AttackResult = GameLiving.eAttackResult.Missed;
					m_handler.MessageToCaster("You miss!", eChatType.CT_YouHit);
					m_handler.MessageToLiving(target, caster.GetName(0, false) + " missed!", eChatType.CT_Missed);
					target.OnAttackedByEnemy(ad, target);
					target.StartInterruptTimer(SPELL_INTERRUPT_DURATION, ad.AttackType, caster);
					return;
				}


				// Block
				bool blocked = false;
				if (target is GamePlayer) 
				{ // mobs left out yet
					GamePlayer player = (GamePlayer)target;
					Weapon lefthand = player.Inventory.GetItem(eInventorySlot.LeftHandWeapon) as Weapon;
					if (lefthand!=null && (player.AttackWeapon==null || player.ActiveWeaponSlot == GameLiving.eActiveWeaponSlot.Standard)) 
					{
						if (target.IsObjectInFront(caster, 180) && lefthand is Shield) 
						{
							// TODO: shield size, which field to use?
							// TODO: 30% chance to block arrows/bolts
							double shield = 0.5 * player.GetModifiedSpecLevel(Specs.Shields);
							double blockchance = ((player.GetModified(eProperty.Dexterity)*2)-100)/40.0 + shield + (0*3) + 5;
							blockchance -= target.GetConLevel(caster) * 5;
							if (blockchance >= 100) blockchance = 99;
							if (blockchance <= 0) blockchance = 1;	
				
							EngageEffect engage = (EngageEffect)target.EffectList.GetOfType(typeof(EngageEffect));
							if (engage!=null && target.AttackState && engage.EngageTarget == caster)
							{	
								// Engage raised block change to 85% if attacker is engageTarget and player is in attackstate							
								// You cannot engage a mob that was attacked within the last X seconds...
								if (engage.EngageTarget.LastAttackedByEnemyTick > engage.EngageTarget.Region.Time - EngageAbilityHandler.ENGAGE_ATTACK_DELAY_TICK) 
								{
									engage.EngageSource.Out.SendMessage(engage.EngageTarget.GetName(0,true)+" has been attacked recently and you are unable to engage.", eChatType.CT_System, eChatLoc.CL_SystemWindow);									
								}  // Check if player has enough endurance left to engage
								else if (engage.EngageSource.EndurancePercent < EngageAbilityHandler.ENGAGE_DURATION_LOST)
								{
									engage.Cancel(false); // if player ran out of endurance cancel engage effect
								} 
								else 
								{
									engage.EngageSource.EndurancePercent -= EngageAbilityHandler.ENGAGE_DURATION_LOST;
									engage.EngageSource.Out.SendMessage("You concentrate on blocking the blow!", eChatType.CT_Skill, eChatLoc.CL_SystemWindow);

									if (blockchance<85)
										blockchance = 85;
								}
							}


							if (blockchance >= Util.Random(1, 100)) 
							{
								m_handler.MessageToLiving(player, "You partially block " + caster.GetName(0, false) + "'s spell!", eChatType.CT_Missed);
								m_handler.MessageToCaster(player.GetName(0, true) + " blocks!", eChatType.CT_YouHit);
								blocked = true;
							}
						}
					}
				}

				// simplified melee damage calculation
				if (blocked == false)
				{
					// TODO: armor resists to damage type

					double damage = m_handler.Spell.Damage / 2; // another half is physical damage
					if(target is GamePlayer)
						ad.ArmorHitLocation = ((GamePlayer)target).CalculateArmorHitLocation();

					Armor armor = null;
					if (target.Inventory != null)
						armor = (Armor) target.Inventory.GetItem((eInventorySlot)ad.ArmorHitLocation);

					double ws = (caster.Level * 8 * (1.0 + (caster.GetModified(eProperty.Dexterity) - 50)/200.0));

					damage *= ((ws + 90.68) / (target.GetArmorAF(ad.ArmorHitLocation) + 20*4.67));
					damage *= 1.0 - Math.Min(0.85, ad.Target.GetArmorAbsorb(ad.ArmorHitLocation));
					ad.Modifier = (int)(damage * (ad.Target.GetResist(ad.DamageType) + SkillBase.GetArmorResist(armor, ad.DamageType)) / -100.0);
					damage += ad.Modifier;
					if (damage < 0) damage = 0;
					ad.Damage += (int)damage;
				}

				// apply total damage cap	
				ad.UncappedDamage = ad.Damage;
				ad.Damage = (int)Math.Min(ad.Damage, m_handler.Spell.Damage * 3);

				if(caster is GamePlayer)
					ad.Damage = (int)(ad.Damage*((GamePlayer)caster).PlayerEffectiveness);

				// fix critical damage
				if (blocked == false && ad.CriticalDamage > 0)
				{
					int critMax = (target is GamePlayer) ? ad.Damage/2 : ad.Damage;
					ad.CriticalDamage = Util.Random(critMax / 10, critMax);
				}

				m_handler.SendDamageMessages(ad);
				m_handler.DamageTarget(ad, false, (blocked ? 0x02 : 0x14));
				target.StartInterruptTimer(SPELL_INTERRUPT_DURATION, ad.AttackType, caster);
			}
		}

		// constructor
		public BoltSpellHandler(GameLiving caster, Spell spell, SpellLine line) : base(caster, spell, line) {}
	}
}
