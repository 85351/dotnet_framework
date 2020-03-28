// ==++==
//
//   Copyright (c) Microsoft Corporation.  All rights reserved.
// 
// ==--==
// =+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+=+
//
// AsynchronousOneToOneChannel.cs
//
// <OWNER>[....]</OWNER>
//
// =-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-

using System.Threading;
using System.Diagnostics.Contracts;

namespace System.Linq.Parallel
{
    /// <summary>
    /// This is a bounded channel meant for single-producer/single-consumer scenarios. 
    /// </summary>
    /// <typeparam name="T">Specifies the type of data in the channel.</typeparam>
    internal sealed class AsynchronousChannel<T> : IDisposable
    {
        // The producer will be blocked once the channel reaches a capacity, and unblocked
        // as soon as a consumer makes room. A consumer can block waiting until a producer
        // enqueues a new element. We use a chunking scheme to adjust the granularity and
        // frequency of synchronization, e.g. by enqueueing/dequeueing N elements at a time.
        // Because there is only ever a single producer and consumer, we are able to acheive
        // efficient and low-overhead synchronization.
        //
        // In general, the buffer has four logical states:
        //     FULL <--> OPEN <--> EMPTY <--> DONE
        //
        // Here is a summary of the state transitions and what they mean:
        //     * OPEN:
        //         A buffer starts in the OPEN state. When the buffer is in the READY state,
        //         a consumer and producer can dequeue and enqueue new elements.
        //     * OPEN->FULL:
        //         A producer transitions the buffer from OPEN->FULL when it enqueues a chunk
        //         that causes the buffer to reach capacity; a producer can no longer enqueue
        //         new chunks when this happens, causing it to block.
        //     * FULL->OPEN:
        //         When the consumer takes a chunk from a FULL buffer, it transitions back from
        //         FULL->OPEN and the producer is woken up.
        //     * OPEN->EMPTY:
        //         When the consumer takes the last chunk from a buffer, the buffer is
        //         transitioned from OPEN->EMPTY; a consumer can no longer take new chunks,
        //         causing it to block.
        //     * EMPTY->OPEN:
        //         Lastly, when the producer enqueues an item into an EMPTY buffer, it
        //         transitions to the OPEN state. This causes any waiting consumers to wake up.
        //     * EMPTY->DONE:
        //         If the buffer is empty, and the producer is done enqueueing new
        //         items, the buffer is DONE. There will be no more consumption or production.
        //
        // Assumptions:
        //   There is only ever one producer and one consumer operating on this channel
        //   concurrently. The internal synchronization cannot handle anything else.
        //
        //   ** WARNING ** WARNING ** WARNING ** WARNING ** WARNING ** WARNING ** WARNING **
        //   VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVV
        //
        //   There... got your attention now... just in case you didn't read the comments
        //   very carefully above, this channel will deadlock, become corrupt, and generally
        //   make you an unhappy camper if you try to use more than 1 producer or more than
        //   1 consumer thread to access this thing concurrently. It's been carefully designed
        //   to avoid locking, but only because of this restriction... 

        private T[][] m_buffer;              // The buffer of chunks.
        private readonly int m_index;            // Index of this channel
        private volatile int m_producerBufferIndex;   // Producer's current index, i.e. where to put the next chunk.
        private volatile int m_consumerBufferIndex;   // Consumer's current index, i.e. where to get the next chunk.

        private volatile bool m_done;        // Set to true once the producer is done.

        private T[] m_producerChunk;         // The temporary chunk being generated by the producer.
        private int m_producerChunkIndex;    // A producer's index into its temporary chunk.
        private T[] m_consumerChunk;         // The temporary chunk being enumerated by the consumer.
        private int m_consumerChunkIndex;    // A consumer's index into its temporary chunk.

        private int m_chunkSize;             // The number of elements that comprise a chunk.

        // These events are used to signal a waiting producer when the consumer dequeues, and to signal a
        // waiting consumer when the producer enqueues.
        private ManualResetEventSlim m_producerEvent;
        private IntValueEvent m_consumerEvent;

        // These two-valued ints track whether a producer or consumer _might_ be waiting. They are marked
        // volatile because they are used in synchronization critical regions of code (see usage below).
        private volatile int m_producerIsWaiting;
        private volatile int m_consumerIsWaiting;
        private CancellationToken m_cancellationToken;

        //-----------------------------------------------------------------------------------
        // Initializes a new channel with the specific capacity and chunk size.
        //
        // Arguments:
        //     orderingHelper - the ordering helper to use for order preservation
        //     capacity   - the maximum number of elements before a producer blocks
        //     chunkSize  - the granularity of chunking on enqueue/dequeue. 0 means default size.
        //
        // Notes:
        //     The capacity represents the maximum number of chunks a channel can hold. That
        //     means producers will actually block after enqueueing capacity*chunkSize
        //     individual elements.
        //

        internal AsynchronousChannel(int index, int chunkSize, CancellationToken cancellationToken, IntValueEvent consumerEvent) :
            this(index, Scheduling.DEFAULT_BOUNDED_BUFFER_CAPACITY, chunkSize, cancellationToken, consumerEvent)
        {
        }

        internal AsynchronousChannel(int index, int capacity, int chunkSize, CancellationToken cancellationToken, IntValueEvent consumerEvent)
        {
            if (chunkSize == 0) chunkSize = Scheduling.GetDefaultChunkSize<T>();

            Contract.Assert(chunkSize > 0, "chunk size must be greater than 0");
            Contract.Assert(capacity > 1, "this impl doesn't support capacity of 1 or 0");

            // Initialize a buffer with enough space to hold 'capacity' elements.
            // We need one extra unused element as a sentinel to detect a full buffer,
            // thus we add one to the capacity requested.
            m_index = index;
            m_buffer = new T[capacity + 1][];
            m_producerBufferIndex = 0;
            m_consumerBufferIndex = 0;

            m_producerEvent = new ManualResetEventSlim();
            m_consumerEvent = consumerEvent;
            m_chunkSize = chunkSize;
            m_producerChunk = new T[chunkSize];
            m_producerChunkIndex = 0;
            m_cancellationToken = cancellationToken;
        }

        //-----------------------------------------------------------------------------------
        // Checks whether the buffer is full. If the consumer is calling this, they can be
        // assured that a true value won't change before the consumer has a chance to dequeue
        // elements. That's because only one consumer can run at once. A producer might see
        // a true value, however, and then a consumer might transition to non-full, so it's
        // not stable for them. Lastly, it's of course possible to see a false value when
        // there really is a full queue, it's all dependent on small race conditions.
        //

        internal bool IsFull
        {
            get
            {
                // Read the fields once. One of these is always stable, since the only threads
                // that call this are the 1 producer/1 consumer threads.
                int producerIndex = m_producerBufferIndex;
                int consumerIndex = m_consumerBufferIndex;


                // Two cases:
                //     1) Is the producer index one less than the consumer?
                //     2) The producer is at the end of the buffer and the consumer at the beginning.

                return (producerIndex == consumerIndex - 1) ||
                    (consumerIndex == 0 && producerIndex == m_buffer.Length - 1);

                // Note to readers: you might have expected us to consider the case where
                // m_producerBufferIndex == m_buffer.Length && m_consumerBufferIndex == 1.
                // That is, a producer has gone off the end of the array, but is about to
                // wrap around to the 0th element again. We don't need this for a subtle
                // reason. It is SAFE for a consumer to think we are non-full when we
                // actually are full; it is NOT for a producer; but thankfully, there is
                // only one producer, and hence the producer will never see this seemingly
                // invalid state. Hence, we're fine producing a false negative. It's all
                // based on a race condition we have to deal with anyway.
            }
        }

        //-----------------------------------------------------------------------------------
        // Checks whether the buffer is empty. If the producer is calling this, they can be
        // assured that a true value won't change before the producer has a chance to enqueue
        // an item. That's because only one producer can run at once. A consumer might see
        // a true value, however, and then a producer might transition to non-empty.
        //

        internal bool IsChunkBufferEmpty
        {
            get
            {
                // The queue is empty when the producer and consumer are at the same index.
                return m_producerBufferIndex == m_consumerBufferIndex;
            }
        }

        //-----------------------------------------------------------------------------------
        // Checks whether the producer is done enqueueing new elements.
        //

        internal bool IsDone
        {
            get { return m_done; }
        }


        //-----------------------------------------------------------------------------------
        // Used by a producer to flush out any internal buffers that have been accumulating
        // data, but which hasn't yet been published to the consumer.
        
        internal void FlushBuffers()
        {
            TraceHelpers.TraceInfo("tid {0}: AsynchronousChannel<T>::FlushBuffers() called",
                                   Thread.CurrentThread.ManagedThreadId);

            // Ensure that a partially filled chunk is made available to the consumer.
            FlushCachedChunk();
        }

        //-----------------------------------------------------------------------------------
        // Used by a producer to signal that it is done producing new elements. This will
        // also wake up any consumers that have gone to sleep.
        //

        internal void SetDone()
        {
            TraceHelpers.TraceInfo("tid {0}: AsynchronousChannel<T>::SetDone() called",
                                   Thread.CurrentThread.ManagedThreadId);

            // This is set with a volatile write to ensure that, after the consumer
            // sees done, they can re-read the enqueued chunks and see the last one we
            // enqueued just above.
            m_done = true;

            // We set the event to ensure consumers that may have waited or are
            // considering waiting will notice that the producer is done. This is done
            // after setting the done flag to facilitate a Dekker-style check/recheck.
            //
            // Because we can ---- with threads trying to Dispose of the event, we must 
            // acquire a lock around our setting, and double-check that the event isn't null.
            //
            // Update 8/2/2011: Dispose() should never be called with SetDone() concurrently,
            // but in order to reduce churn late in the product cycle, we decided not to 
            // remove the lock.
            lock (this)
            {
                if (m_consumerEvent != null)
                {
                    m_consumerEvent.Set(m_index);
                }
            }
        }
        //-----------------------------------------------------------------------------------
        // Enqueues a new element to the buffer, possibly blocking in the process.
        //
        // Arguments:
        //     item                - the new element to enqueue
        //     timeoutMilliseconds - a timeout (or -1 for no timeout) used in case the buffer
        //                           is full; we return false if it expires
        //
        // Notes:
        //     This API will block until the buffer is non-full. This internally buffers
        //     elements up into chunks, so elements are not immediately available to consumers.
        //

        internal void Enqueue(T item)
        {
            // Store the element into our current chunk.
            int producerChunkIndex = m_producerChunkIndex;
            m_producerChunk[producerChunkIndex] = item;

            // And lastly, if we have filled a chunk, make it visible to consumers.
            if (producerChunkIndex == m_chunkSize - 1)
            {
                EnqueueChunk(m_producerChunk);
                m_producerChunk = new T[m_chunkSize];
            }

            m_producerChunkIndex = (producerChunkIndex + 1) % m_chunkSize;
        }

        //-----------------------------------------------------------------------------------
        // Internal helper to queue a real chunk, not just an element.
        //
        // Arguments:
        //     chunk               - the chunk to make visible to consumers
        //     timeoutMilliseconds - an optional timeout; we return false if it expires
        //
        // Notes:
        //     This API will block if the buffer is full. A chunk must contain only valid
        //     elements; if the chunk wasn't filled, it should be trimmed to size before
        //     enqueueing it for consumers to observe.
        //

        private void EnqueueChunk(T[] chunk)
        {
            Contract.Assert(chunk != null);
            Contract.Assert(!m_done, "can't continue producing after the production is over");

            if (IsFull)
                WaitUntilNonFull();
            Contract.Assert(!IsFull, "expected a non-full buffer");

            // We can safely store into the current producer index because we know no consumers
            // will be reading from it concurrently.
            int bufferIndex = m_producerBufferIndex;
            m_buffer[bufferIndex] = chunk;

            // Increment the producer index, taking into count wrapping back to 0. This is a shared
            // write; the CLR 2.0 memory model ensures the write won't move before the write to the
            // corresponding element, so a consumer won't see the new index but the corresponding
            // element in the array as empty.
#pragma warning disable 0420
            Interlocked.Exchange(ref m_producerBufferIndex, (bufferIndex + 1) % m_buffer.Length);
#pragma warning restore 0420

            // (If there is a consumer waiting, we have to ensure to signal the event. Unfortunately,
            // this requires that we issue a memory barrier: We need to guarantee that the write to
            // our producer index doesn't pass the read of the consumer waiting flags; the CLR memory
            // model unfortunately permits this reordering. That is handled by using a CAS above.)

            if (m_consumerIsWaiting == 1 && !IsChunkBufferEmpty)
            {
                TraceHelpers.TraceInfo("AsynchronousChannel::EnqueueChunk - producer waking consumer");
                m_consumerIsWaiting = 0;
                m_consumerEvent.Set(m_index);
            }
        }

        //-----------------------------------------------------------------------------------
        // Just waits until the queue is non-full.
        //

        private void WaitUntilNonFull()
        {
            // We must loop; sometimes the producer event will have been set
            // prematurely due to the way waiting flags are managed.  By looping,
            // we will only return from this method when space is truly available.
            do
            {
                // If the queue is full, we have to wait for a consumer to make room.
                // Reset the event to unsignaled state before waiting.
                m_producerEvent.Reset();

                // We have to handle the case where a producer and consumer are racing to
                // wait simultaneously. For instance, a producer might see a full queue (by
                // reading IsFull just above), but meanwhile a consumer might drain the queue
                // very quickly, suddenly seeing an empty queue. This would lead to deadlock
                // if we aren't careful. Therefore we check the empty/full state AGAIN after
                // setting our flag to see if a real wait is warranted.
#pragma warning disable 0420
                Interlocked.Exchange(ref m_producerIsWaiting, 1);
#pragma warning restore 0420

                // (We have to prevent the reads that go into determining whether the buffer
                // is full from moving before the write to the producer-wait flag. Hence the CAS.)

                // Because we might be racing with a consumer that is transitioning the
                // buffer from full to non-full, we must check that the queue is full once
                // more. Otherwise, we might decide to wait and never be woken up (since
                // we just reset the event).
                if (IsFull)
                {
                    // Assuming a consumer didn't make room for us, we can wait on the event.
                    TraceHelpers.TraceInfo("AsynchronousChannel::EnqueueChunk - producer waiting, buffer full");
                    m_producerEvent.Wait(m_cancellationToken);
                }
                else
                {
                    // Reset the flags, we don't actually have to wait after all.
                    m_producerIsWaiting = 0;
                }
            }
            while (IsFull);
        }

        //-----------------------------------------------------------------------------------
        // Flushes any built up elements that haven't been made available to a consumer yet.
        // Only safe to be called by a producer.
        //
        // Notes:
        //     This API can block if the channel is currently full.
        //

        private void FlushCachedChunk()
        {
            // If the producer didn't fill their temporary working chunk, flushing forces an enqueue
            // so that a consumer will see the partially filled chunk of elements.
            if (m_producerChunk != null && m_producerChunkIndex != 0)
            {
                // Trim the partially-full chunk to an array just big enough to hold it.
                Contract.Assert(1 <= m_producerChunkIndex && m_producerChunkIndex <= m_chunkSize);
                T[] leftOverChunk = new T[m_producerChunkIndex];
                Array.Copy(m_producerChunk, leftOverChunk, m_producerChunkIndex);

                // And enqueue the right-sized temporary chunk, possibly blocking if it's full.
                EnqueueChunk(leftOverChunk);
                m_producerChunk = null;
            }
        }

        //-----------------------------------------------------------------------------------
        // Dequeues the next element in the queue.
        //
        // Arguments:
        //     item - a byref to the location into which we'll store the dequeued element
        //
        // Return Value:
        //     True if an item was found, false otherwise.
        //

        internal bool TryDequeue(ref T item)
        {
            // Ensure we have a chunk to work with.
            if (m_consumerChunk == null)
            {
                if (!TryDequeueChunk(ref m_consumerChunk))
                {
                    Contract.Assert(m_consumerChunk == null);
                    return false;
                }

                m_consumerChunkIndex = 0;
            }

            // Retrieve the current item in the chunk.
            Contract.Assert(m_consumerChunk != null, "consumer chunk is null");
            Contract.Assert(0 <= m_consumerChunkIndex && m_consumerChunkIndex < m_consumerChunk.Length, "chunk index out of bounds");
            item = m_consumerChunk[m_consumerChunkIndex];

            // And lastly, if we have consumed the chunk, null it out so we'll get the
            // next one when dequeue is called again.
            ++m_consumerChunkIndex;
            if (m_consumerChunkIndex == m_consumerChunk.Length)
            {
                m_consumerChunk = null;
            }

            return true;
        }

        //-----------------------------------------------------------------------------------
        // Internal helper method to dequeue a whole chunk.
        //
        // Arguments:
        //     chunk - a byref to the location into which we'll store the chunk
        //
        // Return Value:
        //     True if a chunk was found, false otherwise.
        //

        private bool TryDequeueChunk(ref T[] chunk)
        {
            // This is the non-blocking version of dequeue. We first check to see
            // if the queue is empty. If the caller chooses to wait later, they can
            // call the overload with an event.
            if (IsChunkBufferEmpty)
            {
                return false;
            }

            chunk = InternalDequeueChunk();
            return true;
        }

        //-----------------------------------------------------------------------------------
        // Blocking dequeue for the next element. This version of the API is used when the
        // caller will possibly wait for a new chunk to be enqueued.
        //
        // Arguments:
        //     item      - a byref for the returned element
        //     waitEvent - a byref for the event used to signal blocked consumers
        //
        // Return Value:
        //     True if an element was found, false otherwise.
        //
        // Notes:
        //     If the return value is false, it doesn't always mean waitEvent will be non-
        //     null. If the producer is done enqueueing, the return will be false and the
        //     event will remain null. A caller must check for this condition.
        //
        //     If the return value is false and an event is returned, there have been
        //     side-effects on the channel. Namely, the flag telling producers a consumer
        //     might be waiting will have been set. DequeueEndAfterWait _must_ be called
        //     eventually regardless of whether the caller actually waits or not.
        //

        internal bool TryDequeue(ref T item, ref bool isDone)
        {
            isDone = false;

            // Ensure we have a buffer to work with.
            if (m_consumerChunk == null)
            {
                if (!TryDequeueChunk(ref m_consumerChunk, ref isDone))
                {
                    Contract.Assert(m_consumerChunk == null);
                    return false;
                }

                m_consumerChunkIndex = 0;
            }

            // Retrieve the current item in the chunk.
            Contract.Assert(m_consumerChunk != null, "consumer chunk is null");
            Contract.Assert(0 <= m_consumerChunkIndex && m_consumerChunkIndex < m_consumerChunk.Length, "chunk index out of bounds");
            item = m_consumerChunk[m_consumerChunkIndex];

            // And lastly, if we have consumed the chunk, null it out.
            ++m_consumerChunkIndex;
            if (m_consumerChunkIndex == m_consumerChunk.Length)
            {
                m_consumerChunk = null;
            }

            return true;
        }

        //-----------------------------------------------------------------------------------
        // Internal helper method to dequeue a whole chunk. This version of the API is used
        // when the caller will wait for a new chunk to be enqueued.
        //
        // Arguments:
        //     chunk     - a byref for the dequeued chunk
        //     waitEvent - a byref for the event used to signal blocked consumers
        //
        // Return Value:
        //     True if a chunk was found, false otherwise.
        //
        // Notes:
        //     If the return value is false, it doesn't always mean waitEvent will be non-
        //     null. If the producer is done enqueueing, the return will be false and the
        //     event will remain null. A caller must check for this condition.
        //
        //     If the return value is false and an event is returned, there have been
        //     side-effects on the channel. Namely, the flag telling producers a consumer
        //     might be waiting will have been set. DequeueEndAfterWait _must_ be called
        //     eventually regardless of whether the caller actually waits or not.
        //

        private bool TryDequeueChunk(ref T[] chunk, ref bool isDone)
        {
            isDone = false;

            // We will register our interest in waiting, and then return an event
            // that the caller can use to wait.
            while (IsChunkBufferEmpty)
            {
                // If the producer is done and we've drained the queue, we can bail right away.
                if (IsDone)
                {
                    // We have to see if the buffer is empty AFTER we've seen that it's done.
                    // Otherwise, we would possibly miss the elements enqueued before the
                    // producer signaled that it's done. This is done with a volatile load so
                    // that the read of empty doesn't move before the read of done.
                    if (IsChunkBufferEmpty)
                    {
                        // Return isDone=true so callers know not to wait
                        isDone = true;
                        return false;
                    }
                }

                // We have to handle the case where a producer and consumer are racing to
                // wait simultaneously. For instance, a consumer might see an empty queue (by
                // reading IsChunkBufferEmpty just above), but meanwhile a producer might fill the queue
                // very quickly, suddenly seeing a full queue. This would lead to deadlock
                // if we aren't careful. Therefore we check the empty/full state AGAIN after
                // setting our flag to see if a real wait is warranted.
#pragma warning disable 0420
                Interlocked.Exchange(ref m_consumerIsWaiting, 1);
#pragma warning restore 0420

                // (We have to prevent the reads that go into determining whether the buffer
                // is full from moving before the write to the producer-wait flag. Hence the CAS.)

                // Because we might be racing with a producer that is transitioning the
                // buffer from empty to non-full, we must check that the queue is empty once
                // more. Similarly, if the queue has been marked as done, we must not wait
                // because we just reset the event, possibly losing as signal. In both cases,
                // we would otherwise decide to wait and never be woken up (i.e. deadlock).
                if (IsChunkBufferEmpty && !IsDone)
                {
                    // Note that the caller must eventually call DequeueEndAfterWait to set the
                    // flags back to a state where no consumer is waiting, whether they choose
                    // to wait or not.
                    TraceHelpers.TraceInfo("AsynchronousChannel::DequeueChunk - consumer possibly waiting");
                    return false;
                }
                else
                {
                    // Reset the wait flags, we don't need to wait after all. We loop back around
                    // and recheck that the queue isn't empty, done, etc.
                    m_consumerIsWaiting = 0;
                }
            }

            Contract.Assert(!IsChunkBufferEmpty, "single-consumer should never witness an empty queue here");

            chunk = InternalDequeueChunk();
            return true;
        }

        //-----------------------------------------------------------------------------------
        // Internal helper method that dequeues a chunk after we've verified that there is
        // a chunk available to dequeue.
        //
        // Return Value:
        //     The dequeued chunk.
        //
        // Assumptions:
        //     The caller has verified that a chunk is available, i.e. the queue is non-empty.
        //

        private T[] InternalDequeueChunk()
        {
            Contract.Assert(!IsChunkBufferEmpty);

            // We can safely read from the consumer index because we know no producers
            // will write concurrently.
            int consumerBufferIndex = m_consumerBufferIndex;
            T[] chunk = m_buffer[consumerBufferIndex];

            // Zero out contents to avoid holding on to memory for longer than necessary. This
            // ensures the entire chunk is eligible for GC sooner. (More important for big chunks.)
            m_buffer[consumerBufferIndex] = null;

            // Increment the consumer index, taking into count wrapping back to 0. This is a shared
            // write; the CLR 2.0 memory model ensures the write won't move before the write to the
            // corresponding element, so a consumer won't see the new index but the corresponding
            // element in the array as empty.
#pragma warning disable 0420
            Interlocked.Exchange(ref m_consumerBufferIndex, (consumerBufferIndex + 1) % m_buffer.Length);
#pragma warning restore 0420

            // (Unfortunately, this whole sequence requires a memory barrier: We need to guarantee
            // that the write to m_consumerBufferIndex doesn't pass the read of the wait-flags; the CLR memory
            // model sadly permits this reordering. Hence the CAS above.)

            if (m_producerIsWaiting == 1 && !IsFull)
            {
                TraceHelpers.TraceInfo("BoundedSingleLockFreeChannel::DequeueChunk - consumer waking producer");
                m_producerIsWaiting = 0;
                m_producerEvent.Set();
            }

            return chunk;
        }

        //-----------------------------------------------------------------------------------
        // Clears the flag set when a blocking Dequeue is called, letting producers know
        // the consumer is no longer waiting.
        //

        internal void DoneWithDequeueWait()
        {
            // On our way out, be sure to reset the flags.
            m_consumerIsWaiting = 0;
        }

        //-----------------------------------------------------------------------------------
        // Closes Win32 events possibly allocated during execution.
        //

        public void Dispose()
        {
            // We need to take a lock to deal with consumer threads racing to call Dispose
            // and producer threads racing inside of SetDone.
            //
            // Update 8/2/2011: Dispose() should never be called with SetDone() concurrently,
            // but in order to reduce churn late in the product cycle, we decided not to 
            // remove the lock.
            lock (this)
            {
                Contract.Assert(m_done, "Expected channel to be done before disposing");
                Contract.Assert(m_producerEvent != null);
                Contract.Assert(m_consumerEvent != null);
                m_producerEvent.Dispose();
                m_producerEvent = null;
                m_consumerEvent = null;
            }
        }

    }
}
