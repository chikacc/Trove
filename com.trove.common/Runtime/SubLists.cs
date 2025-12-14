using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Assertions;

namespace Trove
{
    /*
     * The main use case for sub-lists is to provide a solution to the lack of nested collections on entities. Imagine
     * you have a `DynamicBuffer<Item>`, and each `Item` needs a list of `Effect`s, and `Item`s will gain and lose
     * `Effect`s during play. You could choose to give each `Item` an Entity that stores a `DynamicBuffer<Effect>`,
     * but then you have to pay the price of a buffer lookup for each item when accessing `Effect`s. You could choose
     * to store `Effect`s in a `FixedList` in `Item`, but the storage size of that `FixedList` would be limited, and
     * it would no doubt make iterating your `Item`s less efficient if you pick a worst-case-scenario `FixedList` size.
     *
     * With sub-lists, we can solve this problem without paying the price of buffer lookups and without the limitations
     * of FixedLists. Sub-lists use a single buffer in order to store multiple individual lists. In the use case above,
     * each `Item` buffer element would have a sub-list field, and this sub-list would allow them to store a list of
     * `Effect` in a separate `DynamicBuffer<Effect>` on the entity. The `DynamicBuffer<Effect>` would contain all the
     * `Effect`s of all the `Item`s, but special sub-list iterators would allow iterating only the `Effect`s of specific
     * `Item`s in that buffer (without having to check each `Effect` in the buffer to see which `Item` it belongs to).
     *
     * IMPORTANT: when using sub-lists, you must only ever add or remove elements to/from buffers using the sub-list APIs.
     *            The buffer must ONLY contain elements that were added/set using sublist APIs. You must also never
     *            change the data of sub-list interface properties in buffer elements.
     *
     */
    
    #region SubList
    
    /// <summary>
    /// 
    /// Allows storing multiple growable lists in the same buffer.
    /// - Sub-list elements are contiguous in memory.
    /// - Sub-list element indexes in the source buffer can change when list grows
    /// - Good sub-list element add and remove performance.
    /// - The encompassing buffer will grow whenever a new sub-list is created in the buffer, or when an existing
    ///   sub-list needs to reallocate because it grew past capacity.
    ///
    /// This type of sub-list is best suited when sub-list element iteration performance is key, due to sub-list
    /// elements being contiguous in memory. Or when element add/remove performance is key.
    ///
    /// How it works:
    /// - Each sub-list reserves a range a contiguous indexes in the buffer, equivalent to capacity.
    /// - When length would exceed capacity, the sub-list capacity is resized and the sub-list elements are moved to
    ///   another indexes range that can accomodate the new capacity.
    /// - When resizing capacity like this, we first iterate the buffer in order to find an existing free range that
    ///   could accomodate the capacity. If an existing range is not found, we increase the buffer length to accomodate it.
    ///
    /// Usage:
    /// - Create a SubList with SubList.Create(ref buffer);
    /// - Store the created SubList anywhere (in a component, in a buffer element, in a native collection, etc...).
    /// - Add/Remove/Get/Set elements to it using SubList static APIs.
    /// - You can create any amount of SubLists in the same buffer. They will all live alongside eachother in that buffer.
    /// 
    /// </summary>
    
    public interface ISubListElement
    {
        public byte IsAllocated { get; set; }
    }

    public unsafe struct SubListPtr : IComparable<SubListPtr>
    {
        public SubList* Ptr;

        public SubListPtr(SubList* ptr)
        {
            Ptr = ptr;
        }

        public SubListPtr(DynamicBuffer<SubList> subLists, int index)
        {
            Ptr = (SubList*)subLists.GetUnsafePtr() + (long)index;
        }

        public SubListPtr(UnsafeList<SubList> subLists, int index)
        {
            Ptr = subLists.Ptr + (long)index;
        }

        public SubListPtr(NativeList<SubList> subLists, int index)
        {
            Ptr = subLists.GetUnsafePtr() + (long)index;
        }

        public int CompareTo(SubListPtr other)
        {
            return Ptr->ElementsStartIndex.CompareTo(other.Ptr->ElementsStartIndex);
        }
    }
    
    public unsafe struct SubList : IComparable<SubList>
    {
        public int Length;
        public int Capacity;
        public int ElementsStartIndex;

        public int CompareTo(SubList other)
        {
            return ElementsStartIndex.CompareTo(other.ElementsStartIndex);
        }

        public bool IsCreated()
        {
            return ElementsStartIndex >= 0 && Capacity > 0;
        }

        public static SubList Create<T>(ref DynamicBuffer<T> buffer, int initialCapacity)
            where T : unmanaged, ISubListElement
        {
            SubList subList = new SubList();
            subList.Length = 0;
            subList.Capacity = 0;
            subList.ElementsStartIndex = -1;

            SetCapacity(ref subList, ref buffer, math.max(1, initialCapacity));

            return subList;
        }

        public static unsafe void Dispose<T>(ref SubList subList, ref DynamicBuffer<T> buffer)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                void* src = UnsafeUtility.AddressOf(
                    ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(),
                        subList.ElementsStartIndex));
                UnsafeUtility.MemClear(src, sizeof(T) * subList.Capacity);

                subList.Length = 0;
                subList.Capacity = 0;
                subList.ElementsStartIndex = -1;
            }
        }

        /// <summary>
        /// Note: sublists will still grow even with a "growFactor" lower than 1f. But they'll only grow enough to
        /// accomodate what is added. This can be an interesting characteristic if buffer compactness is important.
        /// </summary>
        public static void Add<T>(ref SubList subList, ref DynamicBuffer<T> buffer, T element, float growFactor = 2f)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                int newLength = subList.Length + 1;

                // Grow capacity
                if (newLength > subList.Capacity)
                {
                    int newCapacity = math.max(
                        (int)math.ceil(subList.Capacity * growFactor),
                        subList.Capacity + 1);
                    SetCapacity(ref subList, ref buffer, newCapacity);
                }

                element.IsAllocated = 1;

                buffer[subList.ElementsStartIndex + subList.Length] = element;
                subList.Length = newLength;
            }
            else
            {
                throw new Exception("Error: tried adding to a non-created SubList.");
            }
        }

        public static unsafe bool TryRemoveAt<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int indexInSubList)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                if (indexInSubList >= 0 && indexInSubList < subList.Length)
                {
                    int elementIndexInBuffer = subList.ElementsStartIndex + indexInSubList;
                    int nextElementsLength = subList.Length - indexInSubList - 1;
                    void* src = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(), elementIndexInBuffer + 1));
                    void* dst = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(), elementIndexInBuffer));
                    UnsafeUtility.MemMove(dst, src, sizeof(T) * nextElementsLength);
                    subList.Length--;
                    Assert.IsTrue(subList.Length >= 0);
                    return true;
                }
            }
            else
            {
                throw new Exception("Error: tried removing from a non-created SubList.");
            }

            return false;
        }

        public static bool TryRemoveAtSwapBack<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int indexInSubList)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                if (indexInSubList >= 0 && indexInSubList < subList.Length)
                {
                    int elementIndexInBuffer = subList.ElementsStartIndex + indexInSubList;
                    int lastElementIndexInBuffer = subList.ElementsStartIndex + subList.Length - 1;
                    buffer[elementIndexInBuffer] = buffer[lastElementIndexInBuffer];
                    subList.Length--;
                    Assert.IsTrue(subList.Length >= 0);
                    return true;
                }
            }
            else
            {
                throw new Exception("Error: tried removing from a non-created SubList.");
            }

            return false;
        }

        public static void Clear(ref SubList subList)
        {
            subList.Length = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGet<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int indexInSubList, out T element)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                if (indexInSubList >= 0 && indexInSubList < subList.Length)
                {
                    int indexInBuffer = subList.ElementsStartIndex + indexInSubList;
                    if (indexInBuffer < buffer.Length)
                    {
                        element = buffer[indexInBuffer];
                        return true;
                    }
                }
            }
            else
            {
                throw new Exception("Error: tried getting from a non-created SubList.");
            }

            element = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySet<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int indexInSubList, T element)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                if (indexInSubList >= 0 && indexInSubList < subList.Length)
                {
                    int indexInBuffer = subList.ElementsStartIndex + indexInSubList;
                    if (indexInBuffer < buffer.Length)
                    {
                        element.IsAllocated = 1;
                        buffer[indexInBuffer] = element;
                        return true;
                    }
                }
            }
            else
            {
                throw new Exception("Error: tried setting in a non-created SubList.");
            }

            element = default;
            return false;
        }

        public static unsafe bool SetCapacity<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int newCapacity)
            where T : unmanaged, ISubListElement
        {
            int prevCapacity = subList.Capacity;

            // Grow (or first-time allocate)
            if (newCapacity > prevCapacity)
            {
                // Mark the current occupied as unoccupied before searching
                if (subList.IsCreated())
                {
                    for (int i = subList.ElementsStartIndex; i < subList.ElementsStartIndex + subList.Length; i++)
                    {
                        T iteratedElement = buffer[i];
                        iteratedElement.IsAllocated = 0;
                        buffer[i] = iteratedElement;
                    }
                }

                // Search for available free range
                int freeRangeStart = -1;
                int freeRangeLength = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    T iteratedElement = buffer[i];
                    if (iteratedElement.IsAllocated == 0)
                    {
                        // Detect start of free range
                        if (freeRangeStart < 0)
                        {
                            freeRangeStart = i;
                        }

                        freeRangeLength++;

                        // Detect reached required capacity
                        if (freeRangeLength >= newCapacity)
                        {
                            break;
                        }
                    }
                    // Reset free range
                    else
                    {
                        freeRangeStart = -1;
                        freeRangeLength = 0;
                    }
                }

                // If haven't found a free range, resize buffer to create one
                if (freeRangeLength < newCapacity)
                {
                    int requiredLengthDiff = newCapacity - freeRangeLength;

                    if (freeRangeStart < 0)
                    {
                        freeRangeStart = buffer.Length;
                    }

                    buffer.Resize(buffer.Length + requiredLengthDiff, NativeArrayOptions.ClearMemory);
                }

                // Copy sub-list over to new range,
                if (subList.IsCreated() && subList.Length > 0)
                {
                    void* src = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(),
                            subList.ElementsStartIndex));
                    void* dst = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(), freeRangeStart));
                    UnsafeUtility.MemMove(dst, src, sizeof(T) * subList.Length);

                    // Free previous range
                    UnsafeUtility.MemClear(src, sizeof(T) * subList.Length);
                }

                // Mark the new range as occupied
                for (int i = freeRangeStart; i < freeRangeStart + newCapacity; i++)
                {
                    T iteratedElement = buffer[i];
                    iteratedElement.IsAllocated = 1;
                    buffer[i] = iteratedElement;
                }

                // Update sublist
                subList.Capacity = newCapacity;
                subList.ElementsStartIndex = freeRangeStart;

                return true;
            }
            // Shrink
            else if (subList.IsCreated() && newCapacity < prevCapacity)
            {
                // Can't shrink more than current length
                if (newCapacity >= subList.Length)
                {
                    // Mark the freed range as occupied
                    for (int i = subList.ElementsStartIndex + newCapacity;
                         i < subList.ElementsStartIndex + subList.Capacity;
                         i++)
                    {
                        buffer[i] = new T { IsAllocated = 0 };
                    }

                    subList.Capacity = newCapacity;
                    return true;
                }
            }

            return false;
        }

        public static unsafe void Resize<T>(ref SubList subList, ref DynamicBuffer<T> buffer, int newLength)
            where T : unmanaged, ISubListElement
        {
            if (subList.IsCreated())
            {
                // Only allow growing
                if (newLength > subList.Length)
                {
                    // Check grow capacity
                    if (newLength > subList.Capacity)
                    {
                        SetCapacity(ref subList, ref buffer, newLength);
                    }

                    // Clear elements up to new length
                    for (int i = subList.ElementsStartIndex + subList.Length;
                         i < subList.ElementsStartIndex + newLength;
                         i++)
                    {
                        T elem = buffer[i];
                        elem = new T { IsAllocated = 1 };
                        buffer[i] = elem;
                    }

                    subList.Length = newLength;
                }
            }
            else
            {
                throw new Exception("Error: tried resizing a non-created SubList.");
            }
        }
        
        /// <summary>
        /// TODO: Untested
        /// 
        /// Makes all sublist datas contiguous in memory, with no gap in-between lists, and sets all sublist capacities
        /// to their length. Also trims buffer length and capacity.
        ///
        /// This can be useful for improving performance of iterating all elements of all sublists, reducing memory
        /// usage, or improving efficiency of serialization or netcode synchronization. 
        ///
        /// Note: This is an expensive operation, but can be worth it if the list element counts rarely change.
        /// Note: This modifies the provided sublists data via ptr.
        /// Note: The data of all sublists that were omitted from the sublists list in parameters will disappear.
        /// </summary>
        public static void MakeCompact<T>(ref UnsafeList<SubListPtr> allSubListPtrs, ref DynamicBuffer<T> buffer)
            where T : unmanaged, ISubListElement
        {
            // Sort sublists by startIndex
            allSubListPtrs.Sort();
            
            // Relocate all sublist contents so that they are all next to each other
            int currentAddIndex = 0;
            for (int i = 0; i < allSubListPtrs.Length; i++)
            {
                ref SubList subListRef = ref UnsafeUtility.AsRef<SubList>(allSubListPtrs[i].Ptr);
                
                // Copy list content to new destination, if it's not already there
                if (subListRef.ElementsStartIndex != currentAddIndex)
                {
                    void* src = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(),
                            subListRef.ElementsStartIndex));
                    void* dst = UnsafeUtility.AddressOf(
                        ref UnsafeUtility.ArrayElementAsRef<T>((byte*)buffer.GetUnsafePtr(), 
                            currentAddIndex));
                    UnsafeUtility.MemCpy(dst, src, sizeof(T) * subListRef.Length);
                }
                
                SetCapacity(ref subListRef, ref buffer, subListRef.Length);
                currentAddIndex = subListRef.ElementsStartIndex + subListRef.Length;
            }
            
            // Trim buffer length and capacity
            buffer.ResizeUninitialized(currentAddIndex);
            buffer.Capacity = currentAddIndex;
        }
    }
    
    #endregion
    //
    // #region SubLinkedList
    //
    // /// <summary>
    // /// 
    // /// Allows storing multiple growable lists in the same buffer, as linked lists.
    // /// - Sub-list elements are NOT contiguous in memory.
    // /// - Sub-list elements are NOT guaranteed to keep the same index in the source buffer.
    // /// - Data of the entire buffer always stays compact.
    // /// - The encompassing buffer will grow to accomodate the contents of all sublists when elements are added.
    // ///
    // /// This type of sub-list is best suited when the compactness of the data is key. There will be no "holes" in the
    // /// buffer between sublists or between elements. This makes the memory footprint small, and provides great performance
    // /// when we need to iterate all elements of all sublists. The downsides are poorer iteration performance of a single
    // /// sub-list, and the inability to keep a direct stable index to a sublist element.
    // ///
    // /// How it works:
    // /// - Each sub-list remembers its first element.
    // /// - When length would exceed capacity, the sub-list capacity is resized and the sub-list elements are moved to
    // ///   another indexes range that can accomodate the new capacity.
    // /// - When resizing capacity like this, we first iterate the buffer in order to find an existing free range that
    // ///   could accomodate the capacity. If an existing range is not found, we increase the buffer length to accomodate it.
    // ///
    // /// Usage:
    // /// - Create a SubLinkedList with SubLinkedList.Create();
    // /// - Store the created SubLinkedList anywhere (in a component, in a buffer element, in a native collection, etc...).
    // /// - Add/Remove/Get/Set elements to it using SubLinkedList static APIs.
    // /// - You can create any amount of SubLinkedLists in the same buffer. They will all live alongside eachother in that buffer.
    // /// 
    // /// </summary>
    //
    // public interface ISubLinkedListElement
    // {
    //     public int PrevElementIndex { get; set; }
    //     public int NextElementIndex { get; set; }
    // }
    //
    // public struct SubLinkedList
    // {
    //     public int FirstElementIndex;
    //
    //     public static SubLinkedList Create<T>(ref DynamicBuffer<T> buffer) where T : unmanaged, ISubListElement
    //     {
    //         
    //     }
    // }
    //
    // #endregion

    // #region PooledSubList
    //
    // public interface IPooledSubListElement
    // {
    //     public PooledSubList.InternalElementData PooledSubListData { get; set; }
    // }
    //
    // /// <summary>
    // /// 
    // /// Allows storing multiple growable lists in the same list/buffer.
    // /// - Sub-list elements are not contiguous in memory.
    // /// - Sub-list element indexes do not change after being added.
    // /// - Medium sub-list element remove performance, due to only being able to remove an element by iterating
    // ///   from the last element in the sub-list. Medium sub-list element add performance due to having to
    // ///   search for the lowest free index in the buffer.
    // /// - The encompassing buffer will grow by a certain factor as elements are added, but it will never shrink unless
    // ///   <Trim> is called. But even then, it cannot shrink smaller than the highest-index element in the buffer.
    // ///
    // /// This type of sub-list is best suited for when keeping a stable handle to a specific element is needed. The
    // /// resulting buffer is also likely to be more compact in size than with the regular <SubList>, and add/remove
    // /// performance doesn't decay as much with buffer length as with the <CompactSubList>. 
    // /// 
    // /// </summary>
    // // TODO: optional version relying on FreeIndexRanges?
    // public struct PooledSubList
    // {
    //     public ElementHandle LastElementHandle;
    //     public int Count;
    //
    //     public struct InternalElementData
    //     {
    //         public int Version;
    //         public ElementHandle PrevElementHandle;
    //         // TODO:
    //         public IndexRange FreeRange;
    //     }
    //         
    //     public struct ElementHandle : IEquatable<ElementHandle>
    //     {
    //         public int Index;
    //         public int Version;
    //
    //         public static ElementHandle Null => new ElementHandle { Index = 0, Version = 0 };
    //         
    //         public bool Exists()
    //         {
    //             return Version > 0 && Index >= 0;
    //         }
    //         
    //         public bool Equals(ElementHandle other)
    //         {
    //             return Index == other.Index && Version == other.Version;
    //         }
    //
    //         public override bool Equals(object obj)
    //         {
    //             return obj is ElementHandle other && Equals(other);
    //         }
    //
    //         public override int GetHashCode()
    //         {
    //             return HashCode.Combine(Index, Version);
    //         }
    //
    //         public static bool operator ==(ElementHandle left, ElementHandle right)
    //         {
    //             return left.Equals(right);
    //         }
    //
    //         public static bool operator !=(ElementHandle left, ElementHandle right)
    //         {
    //             return !left.Equals(right);
    //         }
    //     }
    //
    //     public struct Iterator<T>
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         internal int PrevPrevIteratedElementIndex;
    //         internal int PrevIteratedElementIndex;
    //         internal ElementHandle IteratedElementHandle;
    //         internal bool LastGetWasByRef;
    //
    //         [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //         public bool GetNext(in DynamicBuffer<T> buffer, out T iteratedElement, out ElementHandle iteratedElementHandle)
    //         {
    //             LastGetWasByRef = false;
    //             
    //             if (IteratedElementHandle.Exists() && IteratedElementHandle.Index < buffer.Length)
    //             {
    //                 iteratedElement = buffer[IteratedElementHandle.Index];
    //                 if (iteratedElement.PooledSubListData.Version == IteratedElementHandle.Version)
    //                 {
    //                     iteratedElementHandle = IteratedElementHandle;
    //                     return true;
    //                 }
    //
    //                 PrevPrevIteratedElementIndex = PrevIteratedElementIndex;
    //                 PrevIteratedElementIndex = IteratedElementHandle.Index;
    //                 IteratedElementHandle = iteratedElement.PooledSubListData.PrevElementHandle;
    //             }
    //
    //             PrevPrevIteratedElementIndex = -1;
    //             PrevIteratedElementIndex = -1;
    //             iteratedElementHandle = default;
    //             iteratedElement = default;
    //             return false;
    //         }
    //         
    //         [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //         public void RemoveIteratedElement(ref PooledSubList subList, ref DynamicBuffer<T> buffer)
    //         {
    //             int removedElementIndex = PrevIteratedElementIndex;
    //             if (removedElementIndex >= 0)
    //             {
    //                 if (LastGetWasByRef)
    //                 {
    //                     throw new Exception("Cannot remove iterated elements when the element was gotten by ref");
    //                 }
    //
    //                 int removedElementNextIndex = PrevPrevIteratedElementIndex;
    //                 T removedElement = buffer[removedElementIndex];
    //
    //                 // If there was an element after the removed one, update its PrevElement to the removed element's PrevElement
    //                 if (removedElementNextIndex >= 0)
    //                 {
    //                     T removedElementNextElement = buffer[removedElementNextIndex];
    //                     
    //                     InternalElementData removedElementNextElementData = removedElementNextElement.PooledSubListData;
    //                     removedElementNextElementData.PrevElementHandle = removedElement.PooledSubListData.PrevElementHandle;
    //                     removedElementNextElement.PooledSubListData = removedElementNextElementData;
    //                     
    //                     buffer[removedElementNextIndex] = removedElementNextElement;
    //                 }
    //                 // If we're removing the last element, update sub-list
    //                 else
    //                 {
    //                     subList.LastElementHandle = removedElement.PooledSubListData.PrevElementHandle;
    //                 }
    //
    //                 // Write removed element
    //                 InternalElementData removedElementData = removedElement.PooledSubListData;
    //                 removedElementData.Version =  -removedElement.PooledSubListData.Version; // flip version
    //                 removedElement.PooledSubListData = removedElementData;
    //                 buffer[removedElementIndex] = removedElement;
    //                 
    //                 // Update sub-list
    //                 subList.Count--;
    //                 Assert.IsTrue(subList.Count >= 0);
    //             }
    //         }
    //     }
    //     
    //     public static Iterator<T> GetIterator<T>(PooledSubList subList)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         return new Iterator<T>
    //         {
    //             PrevPrevIteratedElementIndex = -1,
    //             PrevIteratedElementIndex = -1,
    //             IteratedElementHandle = subList.LastElementHandle,
    //         };
    //     }
    //
    //     [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //     public static bool Exists<T>(T element)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         return element.PooledSubListData.Version > 0;
    //     }
    //
    //     [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //     public static bool Exists<T>(ref DynamicBuffer<T> buffer, ElementHandle elementHandle)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (elementHandle.Exists() && elementHandle.Index < buffer.Length)
    //         {
    //             return Exists(buffer[elementHandle.Index]);
    //         }
    //
    //         return false;
    //     }
    //     
    //     public static void Add<T>(ref PooledSubList subList, ref DynamicBuffer<T> buffer, T element,
    //         out ElementHandle elementHandle, float growFactor = 1.5f)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         int addIndex = -1;
    //
    //         // Iterate buffer element to try to find a free index
    //         for (int i = 0; i < buffer.Length; i++)
    //         {
    //             T iteratedObject = buffer[i];
    //             if (!Exists(iteratedObject))
    //             {
    //                 addIndex = i;
    //                 break;
    //             }
    //         }
    //
    //         // If haven't found a free index, grow buffer capacity/length
    //         if (addIndex < 0)
    //         {
    //             addIndex = buffer.Length;
    //             int newCapacity = math.max((int)math.ceil(buffer.Length * growFactor), buffer.Length + 1);
    //             Resize(ref buffer, newCapacity);
    //         }
    //
    //         // Write element at free index
    //         T existingElement = buffer[addIndex];
    //         element.PooledSubListData = new InternalElementData
    //         {
    //             Version = -existingElement.PooledSubListData.Version + 1, // flip version and increment
    //             PrevElementHandle = subList.LastElementHandle,
    //         };
    //         buffer[addIndex] = element;
    //         
    //         elementHandle = new ElementHandle
    //         {
    //             Index = addIndex,
    //             Version = element.PooledSubListData.Version,
    //         };
    //         
    //         // Update sub-list
    //         subList.LastElementHandle = elementHandle;
    //         subList.Count++;
    //         Assert.IsTrue(subList.Count >= 0);
    //     }
    //     
    //     [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //     public static bool TryGet<T>(ref DynamicBuffer<T> buffer, ElementHandle elementHandle, out T element)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (elementHandle.Exists() && elementHandle.Index < buffer.Length)
    //         {
    //             element = buffer[elementHandle.Index];
    //             if (element.PooledSubListData.Version == elementHandle.Version)
    //             {
    //                 return true;
    //             }
    //         }
    //
    //         element = default;
    //         return false;
    //     }
    //     
    //     [MethodImpl(MethodImplOptions.AggressiveInlining)]
    //     public static bool TrySet<T>(ref DynamicBuffer<T> buffer, ElementHandle elementHandle, T element)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (elementHandle.Exists() && elementHandle.Index < buffer.Length)
    //         {
    //             T existingElement = buffer[elementHandle.Index];
    //             if (existingElement.PooledSubListData.Version == elementHandle.Version)
    //             {
    //                 element.PooledSubListData = existingElement.PooledSubListData;
    //                 buffer[elementHandle.Index] = element;
    //                 return true;
    //             }
    //         }
    //
    //         return false;
    //     }
    //     
    //     public static bool TryRemove<T>(ref PooledSubList subList, ref DynamicBuffer<T> buffer, ElementHandle elementHandle)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (subList.LastElementHandle.Exists() && elementHandle.Exists())
    //         {
    //             Iterator<T> iterator = GetIterator<T>(subList);
    //             while (iterator.GetNext(in buffer, out T iteratedElement, out ElementHandle iteratedElementHandle))
    //             {
    //                 if (iteratedElementHandle == elementHandle)
    //                 {
    //                     iterator.RemoveIteratedElement(ref subList, ref buffer);
    //                     return true;
    //                 }
    //             }
    //         }
    //         
    //         Assert.IsTrue(subList.Count == 0);
    //         return false;
    //     }
    //
    //     public static void Clear<T>(ref PooledSubList subList, ref DynamicBuffer<T> buffer)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (subList.LastElementHandle.Exists())
    //         {
    //             Iterator<T> iterator = GetIterator<T>(subList);
    //             while (iterator.GetNext(in buffer, out _, out _))
    //             {
    //                 iterator.RemoveIteratedElement(ref subList, ref buffer);
    //             }
    //         }
    //         
    //         Assert.IsTrue(subList.LastElementHandle == ElementHandle.Null);
    //         Assert.IsTrue(subList.Count == 0);
    //     }
    //
    //     /// <summary>
    //     /// Note: can only grow; not shrink
    //     /// </summary>
    //     public static void Resize<T>(ref DynamicBuffer<T> buffer, int newSize)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         if (newSize > buffer.Length)
    //         {
    //             buffer.Resize(newSize, NativeArrayOptions.ClearMemory);
    //         }
    //     }
    //     
    //     public static void Trim<T>(ref DynamicBuffer<T> buffer, bool trimCapacity = false)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         for (int i = buffer.Length - 1; i >= 0; i--)
    //         {
    //             T iteratedElement = buffer[i];
    //             if (Exists(iteratedElement))
    //             {
    //                 buffer.Resize(i + 1, NativeArrayOptions.ClearMemory);
    //                 if (trimCapacity)
    //                 {
    //                     buffer.Capacity = i + 1;
    //                 }
    //
    //                 return;
    //             }
    //         }
    //     }
    //
    //     /// <summary>
    //     /// Reorganizes the pool so that all elements are compact and the pool length fits the elements count exactly.
    //     /// This will invalidate the data of any <PooledSubList>, or of any <ElementHandle> aside from the subList
    //     /// elements' "PrevElementHandle". However, a "allSubListsInBuffer" array will contain the updated SubLists data,
    //     /// and the handlesRemapper will allow remapping old <ElementHandle>s to the updated ones. You will have to handle
    //     /// setting back updated SubLists data and remapping <ElementHandle>s manually.
    //     /// </summary>
    //     public static void MakeCompactAndInvalidateHandles<T>(
    //         ref DynamicBuffer<T> buffer,
    //         ref NativeArray<PooledSubList> allSubListsInBuffer,
    //         ref NativeHashMap<ElementHandle, ElementHandle> handlesRemapper,
    //         bool alsoTrimCapacity)
    //         where T : unmanaged, IPooledSubListElement
    //     {
    //         handlesRemapper.Clear();
    //
    //         // Iterate buffer from the start until we find free indices. When we do, iterate from the end to find an
    //         // existing element to move there. Continue until start iterator and end iterator meet
    //         int dscIndex = buffer.Length - 1;
    //         for (int ascIndex = 0; ascIndex < dscIndex; ascIndex++)
    //         {
    //             T iteratedAscElement = buffer[ascIndex];
    //             if (!Exists(iteratedAscElement))
    //             {
    //                 T iteratedDscElement = default;
    //                 while (dscIndex > ascIndex && !Exists(iteratedDscElement))
    //                 {
    //                     iteratedDscElement = buffer[ascIndex];
    //                     dscIndex--;
    //                 }
    //
    //                 // Move element
    //                 if (Exists(iteratedDscElement))
    //                 {
    //                     handlesRemapper.Add(
    //                         new ElementHandle { Index = dscIndex, Version = iteratedDscElement.PooledSubListData.Version },
    //                         new ElementHandle { Index = ascIndex, Version = iteratedDscElement.PooledSubListData.Version });
    //                     buffer[ascIndex] = iteratedDscElement;
    //                 }
    //             }
    //         }
    //
    //         // Trim excess length (and optionally capacity)
    //         Trim(ref buffer, alsoTrimCapacity);
    //         
    //         // Update elements
    //         for (int i = 0; i < buffer.Length; i++)
    //         {
    //             T iteratedElement = buffer[i];
    //             if (handlesRemapper.TryGetValue(iteratedElement.PooledSubListData.PrevElementHandle, out ElementHandle newPrevElementHandle))
    //             {
    //                 InternalElementData elementData = iteratedElement.PooledSubListData;
    //                 elementData.PrevElementHandle = newPrevElementHandle;
    //                 iteratedElement.PooledSubListData = elementData;
    //             }
    //             buffer[i] = iteratedElement;
    //         }
    //         
    //         // Update sublists
    //         for (int i = 0; i < allSubListsInBuffer.Length; i++)
    //         {
    //             PooledSubList subList = allSubListsInBuffer[i];
    //             if (handlesRemapper.TryGetValue(subList.LastElementHandle, out ElementHandle newLastElementHandle))
    //             {
    //                 subList.LastElementHandle = newLastElementHandle;
    //             }
    //             allSubListsInBuffer[i] = subList;
    //         }
    //     }
    // }
    //
    // #endregion
    //
    
}