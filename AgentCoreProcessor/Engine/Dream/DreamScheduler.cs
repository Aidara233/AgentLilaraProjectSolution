using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;

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
        // 使用 Random.Shared（.NET 6+ 线程安全）
        private readonly List<FragmentDescriptor> _todo = new();
        private readonly List<FragmentDescriptor> _running = new();
        private readonly HashSet<FragmentType> _excludedTypes = new();

        /// <summary>排除某类片段，不会再被 PickRandomType 选中。Consolidation 空 temp 后调用。</summary>
        public void ExcludeType(FragmentType type) => _excludedTypes.Add(type);

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
            int attempted = 0;
            int skippedConflict = 0;
            for (int i = 0; i < maxCount; i++)
            {
                if (!_budget.CanFill) break;

                var type = PickRandomType();
                if (type == null) break;

                attempted++;
                var desc = await _prepareFragment(type.Value);
                if (desc == null) continue;

                if (_memoryTracker.HasConflict(desc.ClaimedMemoryIds))
                {
                    skippedConflict++;
                    Signal.Event(LogGroup.Engine, "FillTodo:记忆冲突跳过",
                        new { type = desc.Type.ToString(), claimedIds = desc.ClaimedMemoryIds.Count });
                    continue;
                }

                _todo.Add(desc);
                added++;
                Signal.Event(LogGroup.Engine, "FillTodo:入队",
                    new { type = desc.Type.ToString(), resourceCost = desc.ResourceCost, estTokens = desc.EstimatedTokens, todoCount = _todo.Count });
            }
            if (attempted > 0 && added == 0)
                Signal.Event(LogGroup.Engine, "FillTodo:全部跳过",
                    new { attempted, skippedConflict, budgetRemaining = _budget.MainBudget });
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

            for (int i = 0; i < _todo.Count; )
            {
                if (_pool.Available <= 0) break;

                var desc = _todo[i];
                if (_pool.TryAcquire(desc.ResourceCost))
                {
                    _memoryTracker.Claim(desc.ClaimedMemoryIds);
                    _todo.RemoveAt(i);
                    _running.Add(desc);
                    desc.RunningTask = Task.Run(() => executeFragment(desc));
                    dispatched.Add(desc);
                    // 不递增 i：RemoveAt 使后续元素前移，当前位置已是下一个
                }
                else
                {
                    i++; // 塞不下则跳过，尝试更小的
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

            if (!_excludedTypes.Contains(FragmentType.Consolidation))
                weights[FragmentType.Consolidation] = 20.0f;
            if (!_excludedTypes.Contains(FragmentType.Weight))
                weights[FragmentType.Weight] = 1.0f;
            if (!_excludedTypes.Contains(FragmentType.Link))
                weights[FragmentType.Link] = 3.0f;
            if (!_excludedTypes.Contains(FragmentType.Combine))
                weights[FragmentType.Combine] = 0.5f;
            if (!_excludedTypes.Contains(FragmentType.Dedup))
                weights[FragmentType.Dedup] = 3.0f;

            if (weights.Count == 0) return null;

            float total = 0;
            foreach (var (_, w) in weights) total += w;

            float roll = (float)(Random.Shared.NextDouble() * total);
            foreach (var (type, w) in weights)
            {
                roll -= w;
                if (roll <= 0) return type;
            }

            return null;
        }
    }
}
