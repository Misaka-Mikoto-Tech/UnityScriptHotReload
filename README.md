# UnityScriptHotReload
本仓库可以实现运行中无感重载C#代码

### 主要功能
Unity开发者经常需要在Editor运行起游戏后调试代码，并临时修改代码测试效果，但Unity自身的运行时重新编译C#代码并重载脚本会造成逻辑中断，因此并不可用。  
本仓库解决了这个问题，可以实时修改代码并无缝使用新代码的逻辑继续运行，并保持内存中的数据和线程上下文均不改变, 而且可以在源文件原地下断点。

### 安装步骤
* 复制 `Assets/HotReload` 目录到您的工程
* 根据需求决定是否勾选菜单 `Tools/HotReload/是否自动重载`, 建议手动重载，避免代码写一半就自动编译的情况

### 使用步骤
1. 点击`Play`按钮启动游戏，测试中发现逻辑错误，假设此场景不容易重现，您并不想停止play修改代码后再重新运行
2. 直接修改C#代码
3. 点击菜单 `【Tools/HotReload/立即重载 (Play时有效) #R】` 或按 `Shift+R`, 片刻后若控制台出现 **热重载完成** 提示则表示已成功重载
4. 继续测试，逻辑已被修改
5. 发现逻辑依旧有错误，重复步骤 2~4

### 功能及限制
* 可以修改原有函数的内容
* 可以新增普通函数
* 可以新增类型
* **不可以修改已存在类型的成员变量**
* **不可以新增虚函数**
* **对新增类型或者函数的访问只能通过原dll已存在函数的代码间接调用，一般情况下无法通过反射调用(patch dll为自增序号命名)**
* **目前尚不支持对泛型方法的热重载，但是修改或者添加的泛型类型/方法被动调用是可以的**

### 测试用例使用步骤
> 测试用例文件为 `Assets/HotReload/TestCase/Scripts/TestDllA/TestDllA_Main.cs`
* 打开场景 `Assets/HotReload/TestCase/Scenes/HotReloadTestScene.unity` 并点击`Play`运行游戏
* 点击场景中的 `Test` 按钮，观察控制台输出
* 启用文件 `TestDll_Main.cs` 顶部的宏定义 `#define APPLY_PATCH`
* 若为手动重载，点击菜单 `【Tools/HotReload/立即重载 (Play时有效) #R】` 或按 Shift+R, 若为自动重载则跳过此条
* 再次点击 `Test` 按钮，对比控制台的输出差异

▶ 执行效果  

* BeforePatch
![Image](Doc/Images/BeforePatch.png)

* AfterPatch0
![Image](Doc/Images/AfterPatch0.png)

* AfterPatch1
![Image](Doc/Images/AfterPatch1.png)

### 最佳实践
* 原地修改C#代码
* 重载脚本(Shift+R或自动)
* 继续运行和操作
* 需要调试时在指定代码行按F9添加断点

### TODO
- [ ] 支持Hook泛型方法
- [ ] 支持添加字段
- [x] 将逻辑拆分为独立进程以实现更高性能

### 实现原理

#### Patch原理
- 本仓库会在运行状态下监控发生改变的文件，根据其所属程序集编译为相应的patch dll。  
- 由于patch dll内的类型和函数、字段等与原始dll虽然名称相同，但实际是不同的类型，为了避免正在运行的代码出现类型校验异常，本方案在新的dll在被载入内存前会使用dnlib对其函数体进行修正，将其对字段和函数的访问全部替换为对同名的原始dll内对应类型的访问，同时清除新dll的相关类型的静态构造函数以避免其逻辑被执行两次（新增类型不清除而是Fix，因其静态构造函数未执行过）。最后将新dll载入内存后执行hook实现patch。

#### 断点问题
* 可以在被修改文件内直接下断点
* 但如果在被修改的文件内原来存在断点，patch后断点会显示为失效的空心状态，原因是此断点关联的是原来的dll，此时可以按两次F9将其改为关联到patch dll

#### 调试 Patcher.exe
> 一般用户可忽略此段
* 使用vs打开 `HotReload/Editor/AssemblyPatcher~/Source/AssemblyPatcher.sln`
* 取消 `HotReloadExecutor.cs` 文件头部 `#define PATCHER_DEBUG` 宏的注释
* 执行Patch时会自动弹出窗口让你选择调试器, attach 即可

#### 相关截图
* 临时文件目录  
![Image](Doc/Images/PatchFilesDir.png)
* 函数重定向
![Image](Doc/Images/method_redirect.png)

* 已存在类型的静态构造函数
![Image](Doc/Images/static_constructor_of_exists_type.png)

* 新类型的静态构造函数
![Image](Doc/Images/static_constructor_of_new_type.png)

### 第三方库
* https://github.com/Misaka-Mikoto-Tech/MonoHook

### 联系方式
* Email: easy66@live.com
* QQ: 372135609