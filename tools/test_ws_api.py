import asyncio, json, websockets

async def test(action, params):
    async with websockets.connect("ws://127.0.0.1:3001/?access_token=alpnapcat") as ws:
        req = {"action": action, "params": params, "echo": "test1"}
        await ws.send(json.dumps(req))
        # 读多条消息，跳过 lifecycle/meta_event，找到匹配 echo 的响应
        for _ in range(10):
            raw = await asyncio.wait_for(ws.recv(), timeout=3)
            msg = json.loads(raw)
            if msg.get("echo") == "test1":
                print(json.dumps(msg, indent=2, ensure_ascii=False))
                return
            # 跳过非目标消息（lifecycle等）
        print("No matching response after 10 messages")

async def main():
    for action, params in [
        ("get_group_root_files", {"group_id": 201644592}),
        ("get_group_files_by_folder", {"group_id": 201644592, "folder_id": "/"}),
        ("get_group_file_system_info", {"group_id": 201644592}),
    ]:
        print(f"\n=== {action} ===")
        try:
            await test(action, params)
        except Exception as e:
            print(f"Error: {e}")

asyncio.run(main())
