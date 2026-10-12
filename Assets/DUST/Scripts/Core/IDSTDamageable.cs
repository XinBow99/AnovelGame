using UnityEngine;

namespace DUST.Core
{
    // 任何可以被打的東西（動物、之後的可破壞物件、敵人）
    public interface IDSTDamageable
    {
        bool IsAlive { get; }
        void TakeDamage(float amount, Transform source);
    }
}
