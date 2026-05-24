using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 片段预准备的结果，进入 todo 前已完成 DB 读取和记忆占用声明。
    /// </summary>
    internal class FragmentDescriptor
    {
        public FragmentType Type { get; init; }
        public int ResourceCost { get; init; }
        public int EstimatedTokens { get; init; }
        public IReadOnlySet<int> ClaimedMemoryIds { get; init; } = new HashSet<int>();
        public object? Payload { get; init; }
        public Task<FragmentResult>? RunningTask { get; set; }
    }

    internal class FragmentResult
    {
        public FragmentDescriptor Descriptor { get; init; } = null!;
        public string? Summary { get; init; }
        public bool Success { get; init; }
    }

    /// <summary>
    /// 资源池：跟踪可用资源数，纯计数器。
    /// </summary>
    internal class DreamResourcePool
    {
        public int TotalResources { get; }
        public int Available { get; private set; }

        public DreamResourcePool(int total)
        {
            TotalResources = total;
            Available = total;
        }

        public bool TryAcquire(int amount)
        {
            if (amount <= 0) return true;
            if (Available >= amount)
            {
                Available -= amount;
                return true;
            }
            return false;
        }

        public void Release(int amount)
        {
            if (amount <= 0) return;
            Available = Math.Min(Available + amount, TotalResources);
        }
    }

    /// <summary>
    /// 主库记忆占用追踪，防止并行片段操作同一批记忆。
    /// </summary>
    internal class DreamMemoryTracker
    {
        private readonly HashSet<int> _claimed = new();

        public bool HasConflict(IReadOnlySet<int> ids)
        {
            if (ids.Count == 0) return false;
            foreach (var id in ids)
                if (_claimed.Contains(id))
                    return true;
            return false;
        }

        public void Claim(IReadOnlySet<int> ids)
        {
            foreach (var id in ids)
                _claimed.Add(id);
        }

        public void Release(IReadOnlySet<int> ids)
        {
            foreach (var id in ids)
                _claimed.Remove(id);
        }
    }

    /// <summary>
    /// 预算状态：主预算耗尽停止填充，增援预算耗尽清空 todo。
    /// </summary>
    internal class BudgetState
    {
        public int MainBudget { get; private set; }
        public int ReserveBudget { get; private set; }
        public int TotalBudget { get; }
        public int TokensUsed { get; private set; }

        public BudgetState(int mainBudget, int reserveBudget)
        {
            MainBudget = mainBudget;
            ReserveBudget = reserveBudget;
            TotalBudget = mainBudget + reserveBudget;
        }

        /// <summary>主预算耗尽后停止 FillTodo。</summary>
        public bool CanFill => MainBudget > 0;

        /// <summary>总预算都耗尽后清空 todo。</summary>
        public bool ShouldClearTodo => MainBudget <= 0 && ReserveBudget <= 0;

        public void Spend(int tokens)
        {
            TokensUsed += tokens;
            var remaining = tokens;
            if (MainBudget > 0)
            {
                var fromMain = Math.Min(MainBudget, remaining);
                MainBudget -= fromMain;
                remaining -= fromMain;
            }
            if (remaining > 0 && ReserveBudget > 0)
            {
                ReserveBudget -= Math.Min(ReserveBudget, remaining);
            }
        }
    }

    /// <summary>
    /// 做梦调度器：管理 todo 列表、运行中片段、资源分配、记忆冲突检测、预算消耗。
    /// 所有调度决策都是单线程串行的，不需要锁。
    /// </summary>
    internal class DreamScheduler
    {
        private readonly DreamResourcePool _pool;
        private readonly DreamMemoryTracker _memoryTracker;
        private readonly BudgetState _budget;
        private readonly DreamConfig _config;
        private readonly Random _rng = new();
        private readonly List<FragmentDescriptor> _todo = new();
        private readonly List<FragmentDescriptor> _running = new();

        /// <summary>准备片段的回调，由 DreamEngine 提供具体 DB 逻辑。</summary>
        private readonly Func<FragmentType, Task<FragmentDescriptor?>> _prepareFragment;

        public DreamScheduler(DreamConfig config, Func<FragmentType, Task<FragmentDescriptor?>> prepareFragment)
        {
            _config = config;
            _pool = new DreamResourcePool(config.TotalResources);
            _memoryTracker = new DreamMemoryTracker();
            _budget = new BudgetState(config.MainTokenBudget, config.ReserveTokenBudget);
            _prepareFragment = prepareFragment;
        }

        // ---- 查询属性 ----

        public bool CanFill => _budget.CanFill;
        public bool HasWork => _todo.Count > 0 || _running.Count > 0;
        public int TodoCount => _todo.Count;
        public int RunningCount => _running.Count;
        public int TokensUsed => _budget.TokensUsed;
        public int AvailableResources => _pool.Available;
        public IReadOnlyList<FragmentDescriptor> Running => _running.AsReadOnly();

        /// <summary>
        /// 尝试填充 todo。加权随机选片段类型，prepare 通过后入队。
        /// 返回实际添加的数量。
        /// </summary>
        public async Task<int> FillTodo(int maxCount)
        {
            int added = 0;
            for (int i = 0; i < maxCount; i++)
            {
                if (!_budget.CanFill) break;

                var type = PickRandomType();
                if (type == null) break;

                var desc = await _prepareFragment(type.Value);
                if (desc == null) continue; // prepare 失败（无可处理的记忆等）

                // 检查记忆冲突
                if (_memoryTracker.HasConflict(desc.ClaimedMemoryIds))
                    continue;

                _todo.Add(desc);
                added++;
            }
            return added;
        }

        /// <summary>
        /// 从 todo 向 running 派发：按资源降序遍历，能塞进资源池就启动。
        /// 返回本次派发的片段列表。
        /// </summary>
        public List<FragmentDescriptor> TryDispatch(Func<FragmentDescriptor, Task<FragmentResult>> executeFragment)
        {
            var dispatched = new List<FragmentDescriptor>();

            // 按资源占用降序排列（大块优先）
            _todo.Sort((a, b) => b.ResourceCost.CompareTo(a.ResourceCost));

            for (int i = _todo.Count - 1; i >= 0; i--)
            {
                if (_pool.Available <= 0) break;

                var desc = _todo[i];
                if (_pool.TryAcquire(desc.ResourceCost))
                {
                    _memoryTracker.Claim(desc.ClaimedMemoryIds);
                    _todo.RemoveAt(i);
                    _running.Add(desc);
                    // 启动执行（fire-and-forget 到 running 列表）
                    desc.RunningTask = Task.Run(() => executeFragment(desc));
                    dispatched.Add(desc);
                }
            }

            return dispatched;
        }

        /// <summary>
        /// 片段完成回调：释放资源、扣除预算、按预算状态清空 todo。
        /// </summary>
        public void OnFragmentComplete(FragmentDescriptor desc)
        {
            _pool.Release(desc.ResourceCost);
            _memoryTracker.Release(desc.ClaimedMemoryIds);
            _running.Remove(desc);
            _budget.Spend(desc.EstimatedTokens);

            if (_budget.ShouldClearTodo)
                _todo.Clear();
        }

        /// <summary>
        /// 加权随机选择片段类型。权重逻辑沿用 ComputeWeights 的思路。
        /// </summary>
        private FragmentType? PickRandomType()
        {
            var weights = new Dictionary<FragmentType, float>();

            // Daydream: only Weight + Link
            // Nap/DeepSleep Phase1: Consolidation dominant, Weight + Link normal
            // DeepSleep Phase2: no Consolidation, all others active
            // For simplicity, always include all types with weights
            if (_config.ConsolidationResourceCost <= _pool.Available)
                weights[FragmentType.Consolidation] = 20.0f; // high weight when temp memories likely exist
            weights[FragmentType.Weight] = 1.0f;
            weights[FragmentType.Link] = 3.0f;
            weights[FragmentType.Combine] = 0.5f;
            weights[FragmentType.Dedup] = 3.0f;

            if (weights.Count == 0) return null;

            float total = 0;
            foreach (var (_, w) in weights) total += w;

            float roll = (float)(_rng.NextDouble() * total);
            foreach (var (type, w) in weights)
            {
                roll -= w;
                if (roll <= 0) return type;
            }

            return null;
        }
    }
}
