using UnityEngine;

namespace DUST.Animals
{
    public enum Temperament
    {
        Passive,   // 不理人，被打才跑（像 Minecraft 的羊）
        Shy,       // 看到人就警戒，太近就跑
        Neutral,   // 平常不理人，被打會反擊（攻擊尚未實作，暫時當成 Shy）
        Hostile,   // 主動攻擊（尚未實作）
    }

    public enum Activity { Any, Diurnal, Crepuscular, Nocturnal }

    // 一個物種的資料。新增物種 = 新增一份這個資產 + 一個 Prefab
    [CreateAssetMenu(menuName = "DUST/Animals/Species Definition", fileName = "SPC_NewSpecies")]
    public class DSTSpeciesDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string displayName = "新物種";
        public string scientificName;
        public GameObject prefab;

        [Header("Stats")]
        public float maxHealth = 10f;
        public float walkSpeed = 0.7f;
        public float runSpeed = 4.2f;
        public float turnSpeedDeg = 300f;
        public float acceleration = 10f;

        [Header("Behaviour")]
        public Temperament temperament = Temperament.Shy;
        public Activity activity = Activity.Any;
        public float noticeRadius = 9f;          // 發現玩家、停下來看
        public float fleeRadius = 4.5f;          // 太近就逃
        public float calmRadius = 16f;           // 離這麼遠才放鬆
        public float sprintNoticeMultiplier = 1.6f;
        public float crouchNoticeMultiplier = 0.45f;
        public float vehicleNoticeMultiplier = 2.0f;
        public Vector2 idleTime = new Vector2(3f, 8f);
        public Vector2 sniffTime = new Vector2(2f, 5f);
        public Vector2 alertTime = new Vector2(0.8f, 2.0f);
        [Range(0, 1)] public float sniffChance = 0.35f;
        public float wanderRadius = 10f;
        public float fleeDistance = 16f;
        [Range(0, 1)] public float herdCohesion = 0f;   // 0 獨居；越大越黏著同伴（閒逛時往群體中心靠，一隻跑全部跑）

        [Header("Spawning (Minecraft-style)")]
        public bool spawns = true;
        public int maxAlive = 4;
        public Vector2Int groupSize = new Vector2Int(1, 1);
        public float minSpawnDistance = 14f;
        public float maxSpawnDistance = 45f;
        public float despawnDistance = 70f;
    }
}
