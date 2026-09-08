namespace DfoServer.Game.Guilds
{
    /// <summary>
    /// 工会职位权限位模型(2016-04-07 公会改版打勾矩阵, 16 位)。
    /// 位号 = 客户端 HasPermission(0x19EA3F0) 的 permId, 位图存
    /// singleton[0x3a5c9a8]+grade*36+0x198, 由 0x0046 内 168B 职位权限表填充。
    /// 位语义来源: 2026-09-04 GLM 对 dnf_hang.dmp 静态反汇编(20 处调用点)。
    /// </summary>
    public static class GuildPermissions
    {
        /// <summary>bit0: 保留位(客户端矩阵保留, 无调用点)。</summary>
        public const int Reserved0 = 0;
        /// <summary>bit1: 收人(邀请/批准入会; 成员菜单 cmd0x1E + 主窗按钮, 疑邀请)。</summary>
        public const int Recruit = 1;
        /// <summary>bit2: 公会频道设置(msg"没有设置公会频道"/"公会频道: ch%02d")。</summary>
        public const int ChannelSetting = 2;
        /// <summary>bit3: 保留位(无调用点)。</summary>
        public const int Reserved3 = 3;
        /// <summary>bit4: 页签/名单编辑(tab 0x9d↔0xe3 切换)。</summary>
        public const int RosterEdit = 4;
        /// <summary>bit5: 修改宣传语【坐实】(使能编辑框+0xdfc, 0x02E3 发送函数读同一控件)。</summary>
        public const int EditPromo = 5;
        /// <summary>bit6: 主窗按钮+0x4d8(功能待定)。</summary>
        public const int MainBtn6 = 6;
        /// <summary>bit7: 主窗按钮+0x4c8/+0x760(功能待定)。</summary>
        public const int MainBtn7 = 7;
        /// <summary>bit8: 公会商店/内容操作(疑, 0x02F8 购买公会属性按此校验)。</summary>
        public const int BuyContent = 8;
        /// <summary>bit9: 同盟操作【客户端特判位: 职位1 或 (singleton+0x270 标志且职位5) 直放,
        ///   不看位图; 服务端仍按位图校验保持一致】。</summary>
        public const int Alliance = 9;
        /// <summary>bit10: 开除成员(参考包权威: ExpelMember; 踢人按此+层级校验)。</summary>
        public const int ExpelMember = 10;
        /// <summary>bit11: 职位任命 + 权限矩阵编辑【坐实】(使能矩阵列与保存钮;
        ///   0x02EB 保存权限 / 0x02EC 职级更名 / 0x007E 调级 按此校验)。</summary>
        public const int Appoint = 11;
        /// <summary>bit12: 委任/转让会长(参考包权威: DelegateMaster, 真实会长专属;
        ///   旧注"任命/卸任副会长级"作废, 0x007E 调级至副会长仍按此位校验)。</summary>
        public const int AppointVice = 12;
        /// <summary>bit13: 支援兵管理(参考包权威: ManageSupporter; 旧注"公告栏"作废,
        ///   公告栏 = bit6)。</summary>
        public const int BoardWrite = 13;
        /// <summary>bit14: 公会邮件(参考包权威: GuildMail)。</summary>
        public const int Reserved14 = 14;
        /// <summary>bit15: 公会仓库【坐实】("尚未拥有公会仓库使用权限" 0x54B1)。</summary>
        public const int Warehouse = 15;

        /// <summary>按位号取位掩码。</summary>
        public static uint Mask(int bit) => 1u << (bit & 31);

        /// <summary>默认矩阵(2026-09-07 对齐 A21-公会修复源码包 GuildRankPolicy;
        ///   客户端无内置默认, SetGradeBitmap 唯一数据源 = 0x0046):
        ///   native grade: 1=会长 2=副会长 3=普通会员 4=新入会员 5=优秀会员
        ///   (客户端 UI 顺序 1,2,5,3,4, 非数值顺序!);
        ///   bit0 与 bits16..31 无契约必须清零;
        ///   会长=bit1..15; 副会长=除 bit3(区域战申请)/bit9(区域战指挥)/bit12(委任);
        ///   优秀=邀请+仓库; 普通=仓库; 新入=无。</summary>
        public static readonly (uint Bitmap, string Name)[] DefaultGradeTable =
        {
            (0x00000000u, ""),              // idx0: 空位(职位值 0 不使用)
            (0x0000FFFEu, "会长"),           // idx1: 会长(bit1..15)
            (0x0000EDF6u, "副会长"),         // idx2: bit1..15 除 3/9/12
            (Mask(Warehouse), "普通会员"),                                    // idx3
            (0x00000000u, "新入会员"),                                        // idx4
            (Mask(Recruit) | Mask(Warehouse), "优秀会员"),                    // idx5
        };

        /// <summary>职位值合法范围(1=会长..5=新入)。</summary>
        public static bool IsValidGrade(int grade) => grade >= 1 && grade <= 5;
    }
}
