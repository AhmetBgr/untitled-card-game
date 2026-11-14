using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;

public class CardModal : MonoBehaviour
{
    public Sprite minionArt;
    public Sprite cardArt;

    public new string name;
    [TextAreaAttribute] public string desc;
    public int attack;
    public int health;
    public int cost;
    public int range;
    public int armor;
    public int collisionDamage;

    public int defHealth;
    public Agent owner;
    public bool isPlayerMinion = true;
    public CardSO upgradedVerdion;
    public bool isUpgraded = false;
    public bool canMove = true;
    public bool canAttack = true;
    public bool canAttackManually = true;
    public int attacksPerTurn = 1;

    public UnityEvent OnPlay;
    public UnityEvent OnDeath;
    public UnityEvent OnTurnStart;
    public UnityEvent OnOwnerTurnEnd;
    public UnityEvent OnAnyMinionSummoned;
    public UnityEvent OnThisMinionSummoned;
    //public UnityEvent OnAnyMinionDied;
    public UnityEvent OnOwnerDrawedCard;
    public UnityEvent OnMinionCollided;
    public UnityEvent OnTookDamage;
    public UnityEvent BonusEvents; // Holds events to be added to another event

    public void UpdateModal(CardSO card, Agent owner, bool isPlayerMinion)
    {
        minionArt = card.minionArt;
        cardArt = card.cardArt;

        name = card.cardName;
        desc = card.desc;
        attack = card.attack;
        health = card.health;
        defHealth = card.defHealth; 
        range = card.range;
        // The battle's weather (Sandstorm) reprices a card the moment it enters play.
        cost = WeatherSystem.AdjustCost(card, card.cost);
        armor = card.armor;
        collisionDamage = card.collisionDamage;
        canMove = card.canMove;
        canAttack = card.canAttack;
        canAttackManually = card.canAttackManually;
        attacksPerTurn = card.attacksPerTurn;
        upgradedVerdion = card.upgradedVersion;
        isUpgraded = card.isUpgraded;

        // Copy event templates from CardSO into this runtime modal.
        // Most are reference-copied (gameplay invokes via the modal).
        OnPlay = card.OnPlay;
        OnDeath = card.OnDeath;
        OnTurnStart = card.OnTurnStart;
        OnOwnerTurnEnd = card.OnOwnerTurnEnd;
        OnAnyMinionSummoned = card.OnAnyMinionSummoned;
        OnThisMinionSummoned = card.OnThisMinionSummoned;
        OnOwnerDrawedCard = card.OnOwnerDrewCard;
        OnMinionCollided = card.OnMinionCollided;
        BonusEvents = card.BonusEvents;

        // OnTookDamage receives runtime AddListener calls (see ActionHolder.AddBonusEventsToMinionsOnTookDamage).
        // Use a per-instance UnityEvent that relays into the SO's event, so runtime listeners
        // don't accumulate on the shared CardSO asset across plays / matches.
        OnTookDamage = new UnityEvent();
        if (card.OnTookDamage != null)
        {
            var soOnTookDamage = card.OnTookDamage;
            OnTookDamage.AddListener(soOnTookDamage.Invoke);
        }
        this.owner = owner;
        this.isPlayerMinion = isPlayerMinion;
    }

    // Copy everything from another modal (e.g. the played hand card's modal, which may carry in-hand
    // buffs) EXCEPT the UnityEvents — those stay the per-instance ones wired up by UpdateModal from
    // the CardSO (notably the OnTookDamage relay), so runtime listeners don't leak onto shared assets.
    public void CopyFrom(CardModal source)
    {
        if (source == null) return;

        minionArt = source.minionArt;
        cardArt = source.cardArt;

        name = source.name;
        desc = source.desc;

        attack = source.attack;
        health = source.health;
        cost = source.cost;
        range = source.range;
        armor = source.armor;
        collisionDamage = source.collisionDamage;
        defHealth = source.defHealth;

        owner = source.owner;
        isPlayerMinion = source.isPlayerMinion;

        upgradedVerdion = source.upgradedVerdion;
        isUpgraded = source.isUpgraded;
        canMove = source.canMove;
        canAttack = source.canAttack;
        canAttackManually = source.canAttackManually;
        attacksPerTurn = source.attacksPerTurn;
    }
}
