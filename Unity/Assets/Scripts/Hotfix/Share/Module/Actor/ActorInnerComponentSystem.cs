﻿using System;
using System.Collections.Generic;
using System.IO;

namespace ET
{
    [FriendOf(typeof(ActorInnerComponent))]
    public static partial class ActorInnerComponentSystem
    {
        public static void HandleIActorResponse(this ActorInnerComponent self, IActorResponse response)
        {
            ActorMessageSender actorMessageSender;
            if (!self.requestCallback.TryGetValue(response.RpcId, out actorMessageSender))
            {
                return;
            }

            self.requestCallback.Remove(response.RpcId);
            
            Run(actorMessageSender, response);
        }
        
        private static void Run(ActorMessageSender self, IActorResponse response)
        {
            if (response.Error == ErrorCore.ERR_ActorTimeout)
            {
                self.Tcs.SetException(new Exception($"Rpc error: request, 注意Actor消息超时，请注意查看是否死锁或者没有reply: actorId: {self.ActorId} {self.Request}, response: {response}"));
                return;
            }

            if (self.NeedException && ErrorCore.IsRpcNeedThrowException(response.Error))
            {
                self.Tcs.SetException(new Exception($"Rpc error: actorId: {self.ActorId} request: {self.Request}, response: {response}"));
                return;
            }

            self.Tcs.SetResult(response);
        }
        
        public static void Reply(this ActorInnerComponent self, Address fromAddress, IActorResponse message)
        {
            self.Send(new ActorId(fromAddress, 0), message);
        }

        public static void Send(this ActorInnerComponent self, ActorId actorId, IActorMessage message)
        {
            Fiber fiber = self.Fiber();
            // 如果发向同一个进程，则扔到消息队列中
            if (actorId.Process != fiber.Process)
            {
                throw new Exception($"actor inner process diff: {actorId.Process} {fiber.Process}");
            }
            ActorMessageQueue.Instance.Send(fiber.Address, actorId, (MessageObject)message);
        }

        private static void SendInner(this ActorInnerComponent self, ActorId actorId, MessageObject message)
        {
            Fiber fiber = self.Fiber();
            ActorMessageQueue.Instance.Send(fiber.Address, actorId, message);
        }

        public static int GetRpcId(this ActorInnerComponent self)
        {
            return ++self.RpcId;
        }

        public static async ETTask<IActorResponse> Call(
                this ActorInnerComponent self,
                ActorId actorId,
                IActorRequest request,
                bool needException = true
        )
        {
            request.RpcId = self.GetRpcId();
            
            if (actorId == default)
            {
                throw new Exception($"actor id is 0: {request}");
            }

            return await self.Call(actorId, request.RpcId, request, needException);
        }
        
        public static async ETTask<IActorResponse> Call(
                this ActorInnerComponent self,
                ActorId actorId,
                int rpcId,
                IActorRequest iActorRequest,
                bool needException = true
        )
        {
            if (actorId == default)
            {
                throw new Exception($"actor id is 0: {iActorRequest}");
            }
            Fiber fiber = self.Fiber();
            if (fiber.Process != actorId.Process)
            {
                throw new Exception($"actor inner process diff: {actorId.Process} {fiber.Process}");
            }
            
            var tcs = ETTask<IActorResponse>.Create(true);

            self.requestCallback.Add(rpcId, new ActorMessageSender(actorId, iActorRequest, tcs, needException));
            
            self.SendInner(actorId, iActorRequest as MessageObject);

            
            async ETTask Timeout()
            {
                await fiber.TimerComponent.WaitAsync(ActorInnerComponent.TIMEOUT_TIME);
                if (!self.requestCallback.TryGetValue(rpcId, out ActorMessageSender action))
                {
                    return;
                }

                self.requestCallback.Remove(rpcId);
                action.Tcs.SetException(new Exception($"actor sender timeout: {iActorRequest}"));
            }
            
            Timeout().Coroutine();
            
            long beginTime = fiber.TimeInfo.ServerFrameTime();

            IActorResponse response = await tcs;
            
            long endTime = fiber.TimeInfo.ServerFrameTime();

            long costTime = endTime - beginTime;
            if (costTime > 200)
            {
                Log.Warning($"actor rpc time > 200: {costTime} {iActorRequest}");
            }
            
            return response;
        }
    }
}