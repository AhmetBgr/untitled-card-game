using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "MinMaxBrain", menuName = "AI/Brains/Min-Max Brain")]
public class MinMaxBrain : AgentBrain
{
    [Header("Action Threshold")]
    [Tooltip("An action must score at least this much for the AI to take it. When nothing on the board " +
             "clears the bar the AI passes instead of dumping its hand. Raise it to make the AI hold cards " +
             "for stronger turns; set it very low (-9999) to restore the old always-act behaviour.")]
    public float minimumActionScore = 0f;

    [Tooltip("Score for a move that accomplishes nothing against the board as it stands — a buff with no " +
             "friendly minion, removal with no enemy minion, a swing that cannot get through armor. Keep " +
             "this comfortably below minimumActionScore so such moves are always declined.")]
    public float deadPlayScore = -100f;

    [Header("Card Play — Mana Efficiency")]
    public float manaEfficiencyWeight = 10f;
    [Tooltip("Per-point penalty for mana left unspent after this card. A ranking tiebreaker only — it can " +
             "never push a card that does something useful below minimumActionScore.")]
    public float unspentManaPenalty = 0.5f;

    [Header("Card Play — Board State")]
    public float minionTempoBonus    = 20f;
    public float perEnemyMinionBonus = 8f;
    public float removalSpellBonus   = 30f;
    public float buffSpellBonus      = 15f;
    public float perFriendlyForBuff  = 5f;
    [Tooltip("Value of a spell with no board target to evaluate — draw, self-only, summon. Positive so " +
             "these are still played; they cannot be judged against the board the way targeted cards can.")]
    public float neutralSpellBonus   = 10f;

    [Header("Attack — Trade Evaluation")]
    public float favorableTradeBonus = 80f;
    public float cleanTradeBonus     = 40f;
    public float suicidePenalty      = -60f;
    public float tempoValueWeight    = 5f;
    public float targetHealthDanger  = 3f;

    [Header("Attack — Hero Targeting")]
    public float heroAttackBonus   = 50f;
    public float heroBiasWhenClear = 70f;

    [Header("Minion Selection — Harmful")]
    public float harmfulHighAttackWeight  = 4f;
    public float harmfulLethalBonus       = 35f;
    public float harmfulOnFriendlyPenalty = -100f;

    [Header("Minion Selection — Beneficial")]
    public float beneficialHighAttackWeight = 3f;
    public float beneficialCanAttackBonus   = 20f;
    public float beneficialOnEnemyPenalty   = -100f;

    [Header("Cell Selection")]
    [Tooltip("Base value of catching one enemy minion in a harmful area.")]
    public float aoeEnemyWeight          = 20f;
    [Tooltip("Base cost of catching one of our own minions in a harmful area. Kept larger in magnitude " +
             "than aoeEnemyWeight so a symmetric hit is a bad hit.")]
    public float aoeFriendlyPenalty      = -25f;
    [Tooltip("Added per point of attack on each minion a harmful area covers — positive for enemies, " +
             "subtracted for our own. Makes the AI aim at the big threats and away from its own big minions.")]
    public float aoeAttackWeight         = 3f;
    [Tooltip("Added when the area's damage would actually kill the covered minion (again, subtracted when " +
             "the victim is ours). Uses the card's aiEffectMagnitude and aiRemovesTarget.")]
    public float aoeKillBonus            = 25f;
    public float beneficialFriendlyWeight = 15f;
    public float beneficialAttackWeight   = 2f;
    [Tooltip("Value per occupied neighbouring cell when placing something with no area to evaluate — " +
             "summons. Keeps new minions clustered rather than dropped in an empty corner.")]
    public float neutralAdjacencyWeight   = 5f;

    // Brain is always on the opponent side; player is always the enemy.
    private Agent Enemy => GameManager.Instance.player;

    public override float MinimumActionScore => minimumActionScore;

    /// <summary>
    /// Scores a card in two separable parts, and that split is what makes declining a play possible.
    ///
    /// <b>Board value</b> is what the card accomplishes against the board as it stands. When the answer
    /// is "nothing" — a buff with no friendly minion to buff, removal with no enemy minion to remove —
    /// the card returns <see cref="deadPlayScore"/> straight away and no later term can rescue it, so it
    /// lands under <see cref="minimumActionScore"/> and OpponentBrained declines to play it.
    ///
    /// <b>Efficiency</b> — mana used, mana left over — only ranks plays that already do something against
    /// each other, and is floored so it can never drag a useful card under the bar. A cheap card early in
    /// the turn is a worse play than an expensive one; it is not a play worth skipping entirely.
    ///
    /// CardController.CanPlay has already dry-run the card, so anything reaching this method can at least
    /// *resolve*. What that dry run cannot tell us is whether resolving actually helps: the selectable
    /// list a damage spell offers contains our own minions too, so "resolvable" includes plays whose only
    /// legal target is our own board. Board value is what catches those.
    /// </summary>
    public override float ScorePlayCard(CardController card, Agent self)
    {
        Agent enemy = Enemy;
        bool isMinion = card.modal.health > 0;

        float value;

        if (isMinion)
        {
            // A body is always worth something, and worth more the more we are being pressured.
            value = minionTempoBonus + enemy.minions.Count * perEnemyMinionBonus;
        }
        else
        {
            CardSO.CardIntent intent = card.card != null ? card.card.aiIntent : CardSO.CardIntent.Neutral;
            bool isRemoval = card.card != null && card.card.aiRemovesTarget;

            if (intent == CardSO.CardIntent.Harmful || isRemoval)
            {
                // Nothing to point it at. Without this the spell scored a flat removalSpellBonus, got
                // played into an empty enemy board, and SelectMinion was left choosing between our own
                // minions — i.e. the AI nuking itself for full price.
                if (enemy.minions.Count == 0) return deadPlayScore;

                float totalThreat = 0f;
                foreach (var m in enemy.minions) totalThreat += m.modal.attack;
                value = removalSpellBonus + totalThreat * 1.5f;
            }
            else if (intent == CardSO.CardIntent.Beneficial)
            {
                if (self.minions.Count == 0) return deadPlayScore;
                value = buffSpellBonus + self.minions.Count * perFriendlyForBuff;
            }
            else
            {
                // Draw, self-only, summon: no board target to judge, so it always carries some value.
                // Without this a neutral spell scored on mana terms alone and could come out negative
                // purely for being cheap, which under the new bar would mean never playing it at all.
                value = neutralSpellBonus;
            }
        }

        float manaRatio = self.availibleMana > 0 ? (float)card.modal.cost / self.availibleMana : 0f;
        float efficiency = manaRatio * manaEfficiencyWeight * card.modal.cost
                         - (self.availibleMana - card.modal.cost) * unspentManaPenalty;

        // Efficiency ranks; it does not veto. Anything that got this far does something useful, so floor
        // it at the bar and let efficiency decide only the order plays come out in.
        return Mathf.Max(value + efficiency, minimumActionScore);
    }

    public override float ScoreAttack(MinionController attacker, MinionController target, Agent self)
    {
        Agent enemy = Enemy;
        bool isHero = target == enemy.hero;

        int effectiveDmgDealt = Mathf.Max(WeatherSystem.ModifyDamage(attacker.modal.attack) - target.modal.armor, 0);

        // The swing cannot get through: no damage, no kill, and in melee we still eat the counter. Checked
        // before the hero bonus so a pointless poke at the enemy hero can't be talked into by heroAttackBonus.
        if (effectiveDmgDealt == 0) return deadPlayScore;

        bool targetWillDie = (target.modal.health - effectiveDmgDealt) <= 0;

        float dist = (target.transform.position - attacker.transform.position).magnitude;
        bool meleeExposure = attacker.modal.range < 2 && dist < 2f;
        int effectiveCounter = meleeExposure ? Mathf.Max(WeatherSystem.ModifyDamage(target.modal.attack) - attacker.modal.armor, 0) : 0;
        bool attackerWillDie = (attacker.modal.health - effectiveCounter) <= 0;

        float score;

        if (targetWillDie && !attackerWillDie)
        {
            // Best case: we kill, we survive.
            score = favorableTradeBonus + target.modal.attack * tempoValueWeight;
        }
        else if (targetWillDie && attackerWillDie)
        {
            // Mutual trade — good if their attack value exceeds ours.
            float tradeValue = target.modal.attack - attacker.modal.attack;
            score = cleanTradeBonus + tradeValue * tempoValueWeight;
        }
        else if (!targetWillDie && !attackerWillDie)
        {
            // Chip damage — prefer chipping high-attack threats.
            score = target.modal.attack * targetHealthDanger;
        }
        else
        {
            // Suicide — we die, they live.
            score = suicidePenalty;
        }

        if (isHero)
            score += enemy.minions.Count == 0 ? heroBiasWhenClear : heroAttackBonus;

        return score;
    }

    public override float ScoreMinionSelection(MinionController candidate, CardSO contextCard, Agent self)
    {
        if (contextCard == null) return 0f;

        bool isFriendly = candidate.owner == self;
        CardSO.CardIntent intent = contextCard.aiIntent;

        // Wrong-side targets stay heavily penalised, but they still rank against each other: when the play
        // is already committed and every candidate is on the wrong side, OpponentBrained.SelectMinion has
        // to pick one, and the least valuable minion is the one to sacrifice. Subtracting attack makes the
        // biggest threat the *worst* choice rather than an arbitrary one.
        if (intent == CardSO.CardIntent.Harmful && isFriendly)
            return harmfulOnFriendlyPenalty - candidate.modal.attack * harmfulHighAttackWeight;

        if (intent == CardSO.CardIntent.Beneficial && !isFriendly)
            return beneficialOnEnemyPenalty - candidate.modal.attack * beneficialHighAttackWeight;

        float score = 0f;

        if (intent == CardSO.CardIntent.Harmful)
        {
            score += candidate.modal.attack * harmfulHighAttackWeight;

            if (contextCard.aiRemovesTarget)
            {
                score += harmfulLethalBonus;
            }
            else if (contextCard.aiEffectMagnitude > 0)
            {
                int effectiveDmg = Mathf.Max(contextCard.aiEffectMagnitude - candidate.modal.armor, 0);
                if (candidate.modal.health <= effectiveDmg)
                    score += harmfulLethalBonus;
            }
        }
        else if (intent == CardSO.CardIntent.Beneficial)
        {
            score += candidate.modal.attack * beneficialHighAttackWeight;

            bool canStillAttack = candidate.attacksMadeThisTurn < candidate.modal.attacksPerTurn && candidate.age > 0;
            if (canStillAttack)
                score += beneficialCanAttackBonus;
        }

        return score;
    }

    /// <summary>
    /// Scores a candidate cell by what the card would actually cover if aimed there — every minion in
    /// <paramref name="footprint"/>, weighted by side, by how big a threat it is, and by whether the
    /// hit would kill it. Harmful areas therefore drift onto enemy clusters and away from our own
    /// minions; beneficial ones do the exact opposite.
    ///
    /// This used to scan a hardcoded 3x3 ring with the centre cell skipped, which matched none of the
    /// four real footprints in ActionHolder: for the plus-shaped area it counted four diagonals the
    /// spell never touches while ignoring the minion standing on the target cell, and for a single-cell
    /// effect it scored only the neighbours and never the cell itself. Aim is now taken from the same
    /// shape that draws the player's highlight and applies the effect.
    /// </summary>
    public override float ScoreCellSelection(Transform cell, CardSO contextCard, Agent self,
                                             Func<Vector2Int, IEnumerable<Vector2Int>> footprint)
    {
        if (GridManager.Instance == null) return 0f;

        Cell centerCell = GridManager.Instance.GetCell(cell.position);
        Vector2Int center = centerCell.index;

        CardSO.CardIntent intent = contextCard != null ? contextCard.aiIntent : CardSO.CardIntent.Neutral;

        // Neutral is overwhelmingly summon placement, whose footprint is the one cell being filled — and
        // that cell has to be empty to be selectable, so scoring the footprint would score zero for every
        // candidate and place minions at random. What matters there is the company the new minion keeps,
        // so this case keeps the neighbourhood scan the whole method used to do.
        if (intent == CardSO.CardIntent.Neutral) return ScoreNeighbourhood(center, self);

        int magnitude = contextCard != null ? contextCard.aiEffectMagnitude : 0;
        bool removesTarget = contextCard != null && contextCard.aiRemovesTarget;

        float score = 0f;

        foreach (Vector2Int index in Covered(footprint, center))
        {
            // IsInsideGrid, not IsOutSideOfGrid: an area overhanging the board edge is normal here and
            // the noisy variant would log a warning for every probe.
            if (!GridManager.Instance.IsInsideGrid(index)) continue;

            Cell covered = GridManager.Instance.GetCell(index);
            if (covered.obj == null) continue;

            MinionController m = covered.obj.GetComponent<MinionController>();
            if (m == null) continue;

            bool isFriendly = m.owner == self;

            if (intent == CardSO.CardIntent.Harmful)
            {
                // Worth of landing this on one minion, before we know whose it is. Counting bodies alone
                // rated three survivors above two corpses and treated our 1/1 as dearly as our 8/8.
                float worth = aoeEnemyWeight + m.modal.attack * aoeAttackWeight;

                bool wouldDie = removesTarget ||
                                (magnitude > 0 && m.modal.health <= Mathf.Max(magnitude - m.modal.armor, 0));
                if (wouldDie) worth += aoeKillBonus;

                // Symmetric: hitting our own costs what hitting theirs earns, offset by the flat penalty
                // that keeps a one-for-one splash unattractive.
                score += isFriendly ? aoeFriendlyPenalty - (worth - aoeEnemyWeight) : worth;
            }
            else
            {
                float worth = beneficialFriendlyWeight + m.modal.attack * beneficialAttackWeight;
                score += isFriendly ? worth : -worth;
            }
        }

        return score;
    }

    // Value of the company a cell keeps, used for placements that have no area to judge.
    private float ScoreNeighbourhood(Vector2Int center, Agent self)
    {
        float score = 0f;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                Vector2Int ni = new Vector2Int(center.x + dx, center.y + dy);
                if (!GridManager.Instance.IsInsideGrid(ni)) continue;

                Cell neighbor = GridManager.Instance.GetCell(ni);
                if (neighbor.obj == null) continue;
                if (neighbor.obj.GetComponent<MinionController>() == null) continue;

                score += neutralAdjacencyWeight;
            }
        }

        return score;
    }

    // The cells a pick at `center` would cover. A null footprint means the caller could not tell us the
    // shape; fall back to the chosen cell alone, which is the one cell every shape includes — guessing
    // wider is what produced the old 3x3 misfire.
    private static IEnumerable<Vector2Int> Covered(
        Func<Vector2Int, IEnumerable<Vector2Int>> footprint, Vector2Int center)
    {
        IEnumerable<Vector2Int> covered = footprint != null ? footprint(center) : null;
        return covered ?? new[] { center };
    }
}
