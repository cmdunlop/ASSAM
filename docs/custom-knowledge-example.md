# 自訂知識檔範例
# 把這個檔案複製到程式資料夾的 UserData\Knowledge\ 裡（檔名隨意，副檔名 .md，UTF-8 編碼），
# 然後在「學習記憶」頁按「重新載入知識庫」，ASSAM 就能回答這些內容。
# 以「## 標題」開始一張知識卡；--- 之前是中繼資料，之後是內文。``` 程式碼區塊內的 ## 不會被當成新卡片。

## 本校實驗室的組譯環境
id: lab-environment
category: 做法
arch: x86
tags: 實驗室, 組譯環境, MASM, Irvine32, VS2019, 作業
---
實驗室電腦使用 Visual Studio 2019 + MASM（ml.exe），Irvine32 函式庫放在 C:\Irvine。
建立專案時請：
1. 使用老師提供的 Project32_VS2019 範本
2. 方案平台選 x86
3. 作業繳交 .asm 原始檔與執行畫面截圖

## Z80 的 LD 指令
id: z80-ld
category: 指令
arch: Z80
tags: Z80, LD, 資料搬移, 載入
---
Z80 用 LD 搬移資料，目的在前：
```
LD A, B        ; A ← B
LD A, (HL)     ; A ← 記憶體[HL]
LD (1234h), A  ; 記憶體[1234h] ← A
LD BC, 1000h   ; 16 位元立即值
```
Z80 沒有 MOV 指令，x86 的 MOV 對應到 Z80 的 LD。
