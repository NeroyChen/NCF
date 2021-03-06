﻿using Senparc.CO2NET.Extensions;
using Senparc.CO2NET.Trace;
using Senparc.Core.Models;
using Senparc.Ncf.Core.Config;
using Senparc.Ncf.Core.Enums;
using Senparc.Ncf.Core.Models;
using Senparc.Ncf.Core.Models.DataBaseModel;
using Senparc.Ncf.Repository;
using Senparc.Ncf.Service;
using Senparc.Ncf.XncfBase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Senparc.Service
{
    public class XncfModuleServiceExtension : XncfModuleService
    {
        private readonly Lazy<SysMenuService> _sysMenuService;
        public XncfModuleServiceExtension(IRepositoryBase<XncfModule> repo, Lazy<SysMenuService> sysMenuService, IServiceProvider serviceProvider) : base(repo, serviceProvider)
        {
            _sysMenuService = sysMenuService;
        }

        /// <summary>
        /// 安装模块
        /// </summary>
        /// <param name="uid">模块 Uid</param>
        /// <returns></returns>
        public async Task<Tuple<PagedList<XncfModule>, string, InstallOrUpdate?>> InstallModuleAsync(string uid)
        {
            if (uid.IsNullOrEmpty())
            {
                throw new Exception("模块不存在！");
            }

            var xncfRegister = Senparc.Ncf.XncfBase.Register.RegisterList.FirstOrDefault(z => z.Uid == uid);
            if (xncfRegister == null)
            {
                throw new Exception("模块不存在！");
            }

            var xncfModule = await base.GetObjectAsync(z => z.Uid == xncfRegister.Uid && z.Version == xncfRegister.Version).ConfigureAwait(false);
            if (xncfModule != null)
            {
                throw new Exception("相同版本模块已安装，无需重复安装！");
            }

            PagedList<XncfModule> xncfModules = await base.GetObjectListAsync(1, 999, _ => true, _ => _.AddTime, Ncf.Core.Enums.OrderingType.Descending).ConfigureAwait(false);

            var xncfModuleDtos = xncfModules.Select(z => base.Mapper.Map<CreateOrUpdate_XncfModuleDto>(z)).ToList();

            //进行模块扫描
            InstallOrUpdate? installOrUpdateValue = null;
            var result = await Senparc.Ncf.XncfBase.Register.ScanAndInstall(xncfModuleDtos, _serviceProvider, async (register, installOrUpdate) =>
            {
                installOrUpdateValue = installOrUpdate;
                //底层系统模块此时还没有设置好初始化的菜单信息，不能设置菜单
                if (register.Uid != Senparc.Ncf.Core.Config.SiteConfig.SYSTEM_XNCF_MODULE_SERVICE_UID &&
                    register.Uid != Senparc.Ncf.Core.Config.SiteConfig.SYSTEM_XNCF_MODULE_AREAS_ADMIN_UID
                    )
                {
                    await InstallMenuAsync(register, installOrUpdate);
                }
            }, uid).ConfigureAwait(false);

            //记录日志
            var installOrUpdateMsg = installOrUpdateValue.HasValue ? (installOrUpdateValue.Value == InstallOrUpdate.Install ? "安装" : "更新") : "失败";
            SenparcTrace.SendCustomLog($"安装或更新模块（{installOrUpdateMsg}）", result.ToString());
            return new Tuple<PagedList<XncfModule>, string, InstallOrUpdate?>(xncfModules, result, installOrUpdateValue);
        }

        /// <summary>
        /// 安装模块之后，安装菜单
        /// </summary>
        /// <param name="register"></param>
        /// <returns></returns>
        public async Task InstallMenuAsync(IXncfRegister register, InstallOrUpdate installOrUpdate)
        {
            var topMenu = await _sysMenuService.Value.GetObjectAsync(z => z.MenuName == "扩展模块").ConfigureAwait(false);
            var currentMenu = await _sysMenuService.Value.GetObjectAsync(z => z.ParentId == topMenu.Id && z.MenuName == register.MenuName).ConfigureAwait(false);
            SysMenuDto menuDto;

            if (installOrUpdate == InstallOrUpdate.Update && currentMenu != null)
            {
                //更新菜单
                menuDto = _sysMenuService.Value.Mapper.Map<SysMenuDto>(currentMenu);
                menuDto.MenuName = register.MenuName;//更新菜单名称
                menuDto.Icon = register.Icon;//更新菜单图标
            }
            else
            {
                //新建菜单
                var icon = register.Icon.IsNullOrEmpty() ? "fa fa-bars" : register.Icon;
                var order = 20;
                switch (register.Uid)
                {
                    case SiteConfig.SYSTEM_XNCF_MODULE_SERVICE_UID:
                        order = 160;
                        break;
                    case SiteConfig.SYSTEM_XNCF_MODULE_AREAS_ADMIN_UID:
                        order = 150;
                        break;
                    default:
                        break;
                }
                menuDto = new SysMenuDto(true, null, register.MenuName, topMenu.Id, $"/Admin/XncfModule/Start/?uid={register.Uid}", icon, order, true, null);
            }

            var sysMemu = await _sysMenuService.Value.CreateOrUpdateAsync(menuDto).ConfigureAwait(false);

            if (installOrUpdate == InstallOrUpdate.Install)
            {
                //更新菜单信息
                SysPermissionDto sysPermissionDto = new SysPermissionDto() 
                {
                     IsMenu = true, ResourceCode = sysMemu.ResourceCode, RoleId = "1", RoleCode = "administrator", PermissionId = sysMemu.Id
                };
                SenparcEntities db = _serviceProvider.GetService<SenparcEntities>();
                db.SysPermission.Add(new SysPermission(sysPermissionDto));
                await db.SaveChangesAsync();
                var updateMenuDto = new UpdateMenuId_XncfModuleDto(register.Uid, sysMemu.Id);
                await base.UpdateMenuId(updateMenuDto).ConfigureAwait(false);
            }
        }
    }
}
