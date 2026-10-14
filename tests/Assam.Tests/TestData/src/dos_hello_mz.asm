; ASSAM 測試用：手工建立的 DOS MZ 執行檔（nasm -f bin dos_hello_mz.asm -o dos_hello_mz.exe）
        bits 16
hdr:    db   'MZ'
        dw   fsize % 512              ; 最後一頁的位元組數
        dw   (fsize + 511) / 512      ; 頁數
        dw   0                        ; 重定位項目數
        dw   2                        ; 檔頭大小（段落，32 位元組）
        dw   10h                      ; 最少額外記憶體
        dw   0FFFFh                   ; 最多額外記憶體
        dw   0                        ; SS
        dw   100h                     ; SP
        dw   0                        ; 檢查碼
        dw   start - code             ; IP
        dw   0                        ; CS
        dw   1Ch                      ; 重定位表位置
        dw   0                        ; 覆蓋編號
        times 32 - ($ - hdr) db 0
code:
msg     db   'Hi MZ$'
start:  mov  ax, cs
        mov  ds, ax
        mov  dx, msg - code
        mov  ah, 9
        int  21h
        mov  ax, 4C00h
        int  21h
fsize   equ  $ - hdr
