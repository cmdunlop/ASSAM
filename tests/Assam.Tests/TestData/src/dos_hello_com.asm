; ASSAM 測試用：DOS COM 檔（nasm -f bin dos_hello_com.asm -o dos_hello.com）
        org  100h
        mov  ah, 09h
        mov  dx, msg
        int  21h
        mov  ax, 4C00h
        int  21h
msg     db   'Hello, DOS!$'
